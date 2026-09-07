using System.Reflection;
using LmKitOmniApi.Application.Abstractions;
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
/// from the approval's own chat session (approval → session → bound custom agent), and
/// these tests pin it against the SHIPPING whitelist gate the dispatcher applies.
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

    private Guid SeedApproval(CustomAgent? agent, string action = "RAG")
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
            Status = "Pending"
        });

        _db.SaveChanges();
        return approvalId;
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
    /// DOCUMENTED RESIDUAL GAP, pinned so it cannot change silently. Deleting a custom
    /// agent NULLs every session's binding to it
    /// (<c>ChatSession.CustomAgentId</c>, <c>DeleteBehavior.SetNull</c> in HermesDbContext),
    /// so after a delete there is nothing left to reconstruct the requesting turn's scope
    /// from and the approval executes unscoped — exactly the pre-fix behaviour. Closing
    /// this last case requires snapshotting the scope onto the approval row itself
    /// (a schema change); tracked in LmKitOmniApi/docs/known-issues.md.
    /// </summary>
    [Fact]
    public async Task ApprovedAction_FallsBackToUnscoped_WhenTheBoundAgentWasDeleted()
    {
        var agent = RestrictedAgent();
        var approvalId = SeedApproval(agent);

        _db.CustomAgents.Remove(agent);
        _db.SaveChanges();

        // The DB itself cleared the binding.
        Assert.False(await _db.ChatSessions.AnyAsync(session => session.CustomAgentId != null));

        var options = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, _tenantId, _userId, approvalId, CancellationToken.None);

        Assert.Null(options);
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
