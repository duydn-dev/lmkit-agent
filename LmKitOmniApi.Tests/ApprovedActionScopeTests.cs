using System.Reflection;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Approvals;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Tools;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Approving a HITL action must never grant MORE authority than the turn that REQUESTED
/// it. <c>ExecuteDirectActionAsync</c> used to call the action dispatcher with
/// <c>options: null</c>, dropping the bound custom agent's <c>AllowedTools</c> whitelist,
/// its <c>KnowledgeDocumentIds</c> RAG scope and <c>AllowWebSearch</c> — so a RAG approval
/// requested inside a document-scoped agent would, once approved, query the WHOLE tenant
/// knowledge base.
///
/// <see cref="AgentOrchestrator.ResolveApprovedActionOptionsAsync"/> recovers that scope
/// from TWO sources and executes under the intersection: the snapshot written onto the
/// approval row when it was created (<c>TaskApproval.RequestOptionsJson</c>) and the live
/// walk approval → session → bound custom agent. These tests pin the result against the
/// SHIPPING whitelist gate the dispatcher applies.
///
/// <para>The snapshot exists because the walk alone was not enough: deleting a custom
/// agent NULLs every session's binding to it, so a pending approval from that session
/// resolved as unbound and ran with NO narrowing at all — more authority than the turn
/// that requested it (known-issues #2). The intersection exists because either source can
/// be stale in the widening direction, and the safe direction must be the default.</para>
/// </summary>
[Collection("DbSqlite")]
public sealed class ApprovedActionScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    // The whitelist gate the approved execution ultimately passes through. Private static
    // on the dispatcher, so it is reached by reflection rather than reimplemented here.
    private static readonly MethodInfo WhitelistGate =
        typeof(AgentActionDispatcher).GetMethod(
            "IsActionWhitelisted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AgentActionDispatcher.IsActionWhitelisted not found — the whitelist gate this "
            + "test pins has moved; update the test rather than deleting it.");

    private static bool DispatcherWouldAllow(string action, AgentRequestOptions? options)
        => options?.AllowedTools is not { } whitelist
           || (bool)WhitelistGate.Invoke(null, new object[] { action, whitelist })!;

    public ApprovedActionScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Approval tenant" });
        _db.Users.Add(NewUser(_userId));
        _db.SaveChanges();
    }

    private User NewUser(Guid id) => new()
    {
        Id = id,
        TenantId = _tenantId,
        Username = $"u{id:N}",
        Email = $"{id:N}@example.test",
        PasswordHash = "x",
        FullName = "Test user"
    };

    /// <summary>
    /// Seeds an approval the way <c>AgentOrchestrator.ExecuteActionWithResilienceAsync</c>
    /// writes one: bound to a session, and carrying the snapshot of the requesting turn's
    /// scope. The snapshot is produced by the SHIPPING serializer
    /// (<see cref="ApprovalScopeSnapshot.Capture"/>) rather than hand-written JSON, so a
    /// change to its format cannot make these tests pass against a payload production
    /// never writes.
    ///
    /// <para><paramref name="snapshotScope"/> defaults to the agent's own scope, which is
    /// what the chat path passes as the turn's options. Pass <c>null</c> explicitly for
    /// <paramref name="captureSnapshot"/>=false to reproduce a row written before this
    /// column existed.</para>
    /// </summary>
    private Guid SeedApproval(
        CustomAgent? agent,
        string action = "RAG",
        bool captureSnapshot = true,
        AgentRequestOptions? snapshotScope = null,
        string? rawSnapshotJson = null)
    {
        if (agent is not null) _db.CustomAgents.Add(agent);

        var sessionId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession
        {
            Id = sessionId,
            TenantId = _tenantId,
            UserId = _userId,
            CustomAgentId = agent?.Id
        });

        var approvalId = Guid.NewGuid();
        _db.TaskApprovals.Add(new TaskApproval
        {
            Id = approvalId,
            TenantId = _tenantId,
            UserId = _userId,
            ChatSessionId = sessionId,
            ActionName = action,
            ParametersJson = "tra cứu tài liệu nội bộ",
            Status = "Pending",
            RequestOptionsJson = rawSnapshotJson
                ?? (captureSnapshot
                    ? ApprovalScopeSnapshot.Capture(snapshotScope ?? ScopeOf(agent))
                    : null)
        });

        _db.SaveChanges();
        return approvalId;
    }

    /// <summary>The per-request options the chat path derives from a bound custom agent.</summary>
    private static AgentRequestOptions? ScopeOf(CustomAgent? agent)
    {
        if (agent is null) return null;
        var tools = LmKitOmniApi.Application.CustomAgents.CustomAgentRules.ParseToolsCsv(agent.AllowedToolsCsv);
        return new AgentRequestOptions
        {
            AllowWebSearch = tools is null || tools.Contains("SearchWeb", StringComparer.OrdinalIgnoreCase),
            AllowedTools = tools,
            KnowledgeDocumentIds = LmKitOmniApi.Application.CustomAgents.CustomAgentRules
                .ParseDocumentIdsCsv(agent.KnowledgeDocumentIdsCsv)
        };
    }

    private CustomAgent RestrictedAgent(Guid? knowledgeDocumentId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        OwnerUserId = _userId,
        Name = "Trợ lý tài liệu",
        PersonaPrompt = "Chỉ trả lời từ tài liệu được ghim.",
        // Deliberately NOT including RunPython / SearchWeb.
        AllowedToolsCsv = "QueryKnowledgeBase,AnalyzeText",
        KnowledgeDocumentIdsCsv = (knowledgeDocumentId ?? Guid.NewGuid()).ToString()
    };

    // ── 1. The fix: an approved action still honours the restrictive whitelist ──

    [Fact]
    public async Task ApprovedAction_HonoursRestrictiveWhitelist()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent);

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.NotNull(options);
        Assert.NotNull(options!.AllowedTools);
        Assert.Equal(new[] { "QueryKnowledgeBase", "AnalyzeText" }, options.AllowedTools!.ToArray());

        // Through the dispatcher's own gate: the whitelisted action runs, the others are
        // refused — the approval grants no more than the requesting turn had.
        Assert.True(DispatcherWouldAllow("RAG", options));
        Assert.True(DispatcherWouldAllow("NLP", options));
        Assert.False(DispatcherWouldAllow("PYTHON", options));
        Assert.False(DispatcherWouldAllow("CODE", options));
        Assert.False(DispatcherWouldAllow("BROWSE", options));
        Assert.False(DispatcherWouldAllow("WEB_SEARCH", options));
        Assert.False(DispatcherWouldAllow("MCP:anything", options));
    }

    [Fact]
    public async Task ApprovedAction_KeepsTheRagKnowledgeScope()
    {
        var pinned = Guid.NewGuid();
        var approvalId = SeedApproval(RestrictedAgent(pinned));

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        // The leak this test exists for: with null options the approved RAG query hit the
        // entire tenant knowledge base instead of the agent's pinned document.
        Assert.NotNull(options?.KnowledgeDocumentIds);
        Assert.Equal(new[] { pinned }, options!.KnowledgeDocumentIds!.ToArray());
    }

    [Fact]
    public async Task ApprovedAction_DoesNotRestoreWebSearch_WhenTheAgentDoesNotWhitelistIt()
    {
        var approvalId = SeedApproval(RestrictedAgent());

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.False(options!.AllowWebSearch);
    }

    [Fact]
    public async Task ApprovedAction_UsesTheAgentsCurrentScope_WhenItNarrowedWhilePending()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent);

        // The owner narrows the agent while the approval sits pending.
        agent.AllowedToolsCsv = "AnalyzeText";
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.False(DispatcherWouldAllow("RAG", options));
        Assert.True(DispatcherWouldAllow("NLP", options));
    }

    // ── 2. Fail-closed when the scope cannot be reconstructed ──

    /// <summary>
    /// THE SECURITY FIX (known-issues #2, now closed). Deleting a custom agent NULLs every
    /// session's binding to it (<c>ChatSession.CustomAgentId</c>,
    /// <c>DeleteBehavior.SetNull</c> in HermesDbContext), so the session walk reports
    /// "unbound" — and unbound used to mean UNSCOPED. A tool call the user had restricted
    /// at request time then executed with no narrowing at all: a RAG approval raised inside
    /// a document-scoped agent queried the whole tenant knowledge base, and a whitelist
    /// that excluded PYTHON stopped excluding it.
    ///
    /// <para>The scope is now snapshotted onto the approval row when it is created, and the
    /// row survives the delete. This test asserts the exact scope the requesting turn ran
    /// under, through the dispatcher's own whitelist gate — the inverse of what it pinned
    /// before the fix.</para>
    /// </summary>
    [Fact]
    public async Task ApprovedAction_KeepsTheRequestingTurnsScope_WhenTheBoundAgentWasDeleted()
    {
        var pinned = Guid.NewGuid();
        var agent = RestrictedAgent(pinned);
        var approvalId = SeedApproval(agent);

        _db.CustomAgents.Remove(agent);
        _db.SaveChanges();

        // The DB itself cleared the binding, so the session walk has nothing left to say.
        Assert.False(await _db.ChatSessions.AnyAsync(session => session.CustomAgentId != null));

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.NotNull(options);
        Assert.Equal(new[] { "QueryKnowledgeBase", "AnalyzeText" }, options!.AllowedTools!.ToArray());
        Assert.Equal(new[] { pinned }, options.KnowledgeDocumentIds!.ToArray());
        Assert.False(options.AllowWebSearch);

        // The whole point, through the gate the approved execution actually passes:
        Assert.True(DispatcherWouldAllow("RAG", options));
        Assert.False(DispatcherWouldAllow("PYTHON", options));
        Assert.False(DispatcherWouldAllow("MCP:anything", options));
    }

    /// <summary>
    /// A row written before the snapshot column existed still behaves exactly as it did
    /// then — the fallback is the session walk, which is right for every case but the
    /// deleted-agent one, and no migration could have reconstructed that.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_FallsBackToTheSessionWalk_ForAPreSnapshotRow()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent, captureSnapshot: false);

        Assert.Null(await _db.TaskApprovals.AsNoTracking()
            .Where(t => t.Id == approvalId).Select(t => t.RequestOptionsJson).SingleAsync());

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.Equal(new[] { "QueryKnowledgeBase", "AnalyzeText" }, options!.AllowedTools!.ToArray());
    }

    /// <summary>
    /// And the pre-snapshot row whose agent was deleted keeps the OLD behaviour, unscoped,
    /// because there is genuinely nothing left to recover. Stated rather than hidden: the
    /// fix protects rows created from this deploy onward, not rows that were already
    /// pending when it shipped.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_StaysUnscoped_ForAPreSnapshotRowWhoseAgentWasDeleted()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent, captureSnapshot: false);
        _db.CustomAgents.Remove(agent);
        _db.SaveChanges();

        Assert.Null(await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None));
    }

    /// <summary>
    /// The snapshot never WIDENS either. An agent narrowed while the approval sat pending
    /// is honoured at its narrower setting, exactly as before the snapshot existed — the
    /// resolver takes the intersection of the two, not whichever it read first.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_UsesTheNarrowerOfSnapshotAndAgent_WhenTheAgentNarrowedWhilePending()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent); // snapshot: QueryKnowledgeBase + AnalyzeText

        agent.AllowedToolsCsv = "AnalyzeText";
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.Equal(new[] { "AnalyzeText" }, options!.AllowedTools!.ToArray());
        Assert.False(DispatcherWouldAllow("RAG", options));
        Assert.True(DispatcherWouldAllow("NLP", options));
    }

    /// <summary>
    /// The other direction of the same rule: an agent WIDENED while the approval sat
    /// pending does not widen the approval. The requesting turn could not run PYTHON, so
    /// neither can its approval.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_IsNotWidened_WhenTheAgentGainedToolsWhilePending()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent);

        agent.AllowedToolsCsv = "QueryKnowledgeBase,AnalyzeText,RunPython,SearchWeb";
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.Equal(new[] { "QueryKnowledgeBase", "AnalyzeText" }, options!.AllowedTools!.ToArray());
        Assert.False(options.AllowWebSearch);
        Assert.False(DispatcherWouldAllow("PYTHON", options));
        Assert.False(DispatcherWouldAllow("WEB_SEARCH", options));
    }

    /// <summary>
    /// The document pin is intersected the same way — and when the two pins share NOTHING
    /// the answer is deny-all, not "no documents". An empty document allowlist is read by
    /// <c>RagPipelineService</c> as NO RESTRICTION, so writing the empty intersection would
    /// silently widen retrieval from one pinned document to the whole tenant knowledge
    /// base: the exact leak this whole mechanism exists to prevent.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_DeniesEverything_WhenTheAgentsPinnedDocumentsWereEntirelyReplaced()
    {
        var agent = RestrictedAgent(Guid.NewGuid());
        var approvalId = SeedApproval(agent);

        agent.KnowledgeDocumentIdsCsv = Guid.NewGuid().ToString();
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.NotNull(options?.AllowedTools);
        Assert.Empty(options!.AllowedTools!);
        Assert.False(DispatcherWouldAllow("RAG", options));
    }

    /// <summary>
    /// A snapshot that is present but unreadable is NOT an absent snapshot: the requesting
    /// turn was narrowed by something this code can no longer parse, so the approval is
    /// refused rather than run wide open. This is why the column is stored in plain text —
    /// encrypting it would put the data-protection keyring on this path, and a rotated key
    /// would land every pending approval in the system here.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_DeniesEverything_WhenTheSnapshotCannotBeParsed()
    {
        var approvalId = SeedApproval(agent: null, rawSnapshotJson: "{ not json at all");

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.NotNull(options?.AllowedTools);
        Assert.Empty(options!.AllowedTools!);
        Assert.False(options.AllowWebSearch);
        Assert.False(DispatcherWouldAllow("RAG", options));
    }

    /// <summary>
    /// The deny-all from an un-shared agent survives the intersection: a snapshot can never
    /// re-open a scope the live walk has closed.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_StaysDenyAll_WhenAnUnsharedAgentMeetsAGenerousSnapshot()
    {
        var otherOwner = NewUser(Guid.NewGuid());
        _db.Users.Add(otherOwner);
        _db.SaveChanges();

        var agent = RestrictedAgent();
        agent.OwnerUserId = otherOwner.Id;
        agent.IsSharedWithTenant = true;
        var approvalId = SeedApproval(agent);

        agent.IsSharedWithTenant = false;
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.Empty(options!.AllowedTools!);
        Assert.False(options.AllowWebSearch);
    }

    [Fact]
    public async Task ApprovedAction_DeniesEverything_WhenABoundAgentWasUnsharedByAnotherOwner()
    {
        var otherOwner = NewUser(Guid.NewGuid());
        _db.Users.Add(otherOwner);
        _db.SaveChanges();

        var agent = RestrictedAgent();
        agent.OwnerUserId = otherOwner.Id;
        agent.IsSharedWithTenant = true;
        var approvalId = SeedApproval(agent);

        agent.IsSharedWithTenant = false;
        _db.SaveChanges();

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        // The binding survives (the agent row still exists) but its scope is no longer
        // readable by this caller. Running unscoped would widen authority, so the
        // approved execution is refused instead.
        Assert.NotNull(options?.AllowedTools);
        Assert.Empty(options!.AllowedTools!);
        Assert.False(options.AllowWebSearch);
        Assert.False(DispatcherWouldAllow("RAG", options));
        Assert.False(DispatcherWouldAllow("PYTHON", options));
    }

    // ── 3. Unscoped turns keep behaving exactly as before ──

    [Fact]
    public async Task ApprovedAction_StaysUnscoped_WhenTheSessionHasNoBoundAgent()
    {
        var approvalId = SeedApproval(agent: null);

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        // Null == the pre-fix behaviour, and correct here: that turn was unscoped too.
        Assert.Null(options);
        Assert.True(DispatcherWouldAllow("PYTHON", options));
    }

    [Fact]
    public async Task ApprovedAction_StaysUnscoped_WhenNoApprovalIdIsSupplied()
    {
        Assert.Null(await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId: null, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovedAction_StaysUnscoped_ForAnApprovalBelongingToAnotherUser()
    {
        var approvalId = SeedApproval(RestrictedAgent());

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, Guid.NewGuid(), approvalId, CancellationToken.None);

        // The approval is not this caller's, so nothing is recovered from it; the caller
        // is separately rejected by the approvals handler's own tenant/user filter.
        Assert.Null(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
