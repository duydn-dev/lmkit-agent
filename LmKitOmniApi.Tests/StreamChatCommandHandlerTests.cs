using System.Runtime.CompilerServices;
using System.Text.Json;
using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Chat.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// T15 — behavioural tests for <see cref="StreamChatCommandHandler"/>, the 585-line
/// core of the product that had no real coverage.
///
/// <para>The one existing test that touches this handler
/// (<c>ApiIntegrationTests.ChatAttachment_IsDeletedAfterRequestProcessing</c>)
/// passes on the TOTAL-FAILURE path: the controller writes <c>[DONE]</c>
/// unconditionally after swallowing any exception, so it would still pass if the
/// handler body were <c>throw new Exception()</c>. Nothing below can pass on that
/// path — every test asserts a specific state change the handler must produce.</para>
///
/// <para>The model boundary is faked, not loaded: <see cref="LmModelManager"/>
/// short-circuits on its cached model field, so an uninitialized <see cref="LM"/>
/// is enough to build the <see cref="ChatHistory"/> the handler hands to the
/// orchestrator — and that history is exactly what these tests inspect.</para>
/// </summary>
public sealed class StreamChatCommandHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly RecordingOrchestrator _orchestrator = new();
    private readonly ScriptedTokenManagement _tokens = new();
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    public StreamChatCommandHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        _db.Users.Add(new User
        {
            Id = _userId,
            TenantId = _tenantId,
            Username = "u",
            Email = "u@t.test",
            PasswordHash = "x",
            Role = "User"
        });
        _db.SaveChanges();
    }

    // ── the happy path the existing integration test never pins ───────────

    [Fact]
    public async Task NormalSend_StreamsTheOrchestratorOutput_PersistsBothTurns_AndAutoTitlesTheSession()
    {
        SeedSession(title: CreateChatSessionCommand.DefaultChatTitle);
        _orchestrator.Script = ["Xin ", "chào ", "bạn."];

        var streamed = await CollectAsync(Handler().Handle(Command("Câu hỏi của tôi"), CancellationToken.None));

        Assert.Equal(["Xin ", "chào ", "bạn."], streamed);
        Assert.Equal("Câu hỏi của tôi", _orchestrator.LastQuery);
        Assert.Equal(_tenantId, _orchestrator.LastTenantId);
        Assert.Equal("User", _orchestrator.LastUserRole);

        using var verify = NewContext();
        var stored = verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == _sessionId).OrderBy(m => m.CreatedAt).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal("user", stored[0].Role);
        Assert.Equal("Câu hỏi của tôi", stored[0].Content);
        Assert.Equal("assistant", stored[1].Role);
        Assert.Equal("Xin chào bạn.", stored[1].Content);

        // A default-titled session is auto-titled from the first message.
        Assert.Equal("Câu hỏi của tôi", verify.ChatSessions.AsNoTracking().Single(s => s.Id == _sessionId).Title);
    }

    [Fact]
    public async Task Send_ToAForeignSession_IsRejectedBeforeAnythingIsWritten()
    {
        SeedSession();
        var command = Command("xin chào");
        command.UserId = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await CollectAsync(Handler().Handle(command, CancellationToken.None)));

        Assert.Equal(0, _orchestrator.Calls);
        using var verify = NewContext();
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
    }

    // ── history trimming + summary injection ──────────────────────────────

    [Fact]
    public async Task History_IsLoadedChronologically_TrimmedByTheTokenService_AndItsSummaryIsInjectedAndStored()
    {
        SeedSession();
        SeedConversation(("user", "câu 1"), ("assistant", "đáp 1"), ("user", "câu 2"), ("assistant", "đáp 2"));

        // The trim service drops the oldest exchange and hands back a summary of it,
        // which the handler must inject into the model history AND store on the session.
        _tokens.Trim = messages => new TrimmedHistoryResult
        {
            Messages =
            [
                new HistoryMessage { Role = "system", Content = "Tóm tắt: đã nói về X." },
                .. messages.TakeLast(2)
            ],
            ConversationSummary = "Tóm tắt: đã nói về X.",
            RemovedMessageCount = 2
        };

        await CollectAsync(Handler().Handle(Command("câu 3"), CancellationToken.None));

        // The service saw the full stored history, oldest first.
        Assert.Equal(
            ["câu 1", "đáp 1", "câu 2", "đáp 2"],
            _tokens.LastInput!.Select(m => m.Content).ToArray());

        // The model history carries the trimmed window, and the summary is injected as
        // USER context (the handler maps role "system" to AuthorRole.User).
        //
        // Note the shape: LM-Kit's ChatHistory MERGES consecutive same-role turns with
        // a newline, so the injected summary is glued onto the user turn that follows
        // it rather than standing as its own block — the model receives ONE user turn
        // containing "<summary>\n<message>", not two. Pinned here because it is not
        // obvious from the handler's AddMessage loop.
        var history = _orchestrator.LastHistoryRoles!;
        Assert.Equal(
            [(AuthorRole.User, "Tóm tắt: đã nói về X.\ncâu 2"), (AuthorRole.Assistant, "đáp 2")],
            history);

        using var verify = NewContext();
        Assert.Equal("Tóm tắt: đã nói về X.",
            verify.ChatSessions.AsNoTracking().Single(s => s.Id == _sessionId).Summary);
    }

    [Fact]
    public async Task History_IsCappedAtFiftyMessages_KeepingTheNewest()
    {
        SeedSession();
        SeedConversation(Enumerable.Range(1, 60)
            .Select(i => (i % 2 == 1 ? "user" : "assistant", $"tin nhắn {i}")).ToArray());

        await CollectAsync(Handler().Handle(Command("mới"), CancellationToken.None));

        var loaded = _tokens.LastInput!;
        Assert.Equal(50, loaded.Count);
        // Newest 50, still in chronological order.
        Assert.Equal("tin nhắn 11", loaded[0].Content);
        Assert.Equal("tin nhắn 60", loaded[^1].Content);
    }

    [Fact]
    public async Task History_PrefersTheCachedSnapshotOverTheDatabaseOnANormalSend()
    {
        SeedSession();
        SeedConversation(("user", "chỉ có trong DB"));
        await _cache.SetStringAsync(CacheKey, JsonSerializer.Serialize(new List<HistoryMessage>
        {
            new() { Role = "user", Content = "chỉ có trong cache", CreatedAt = DateTime.UtcNow.AddMinutes(-1) }
        }));

        await CollectAsync(Handler().Handle(Command("tiếp"), CancellationToken.None));

        Assert.Equal(["chỉ có trong cache"], _tokens.LastInput!.Select(m => m.Content).ToArray());
    }

    // ── ephemeral ("Chat tạm thời") branch ────────────────────────────────

    [Theory]
    [InlineData(true, false)]  // session already ephemeral
    [InlineData(false, true)]  // this send opts in
    public async Task EphemeralSend_StreamsNormallyButPersistsNoTurns_AndSkipsTitleAndSummary(
        bool sessionEphemeral, bool requestEphemeral)
    {
        SeedSession(title: CreateChatSessionCommand.DefaultChatTitle, ephemeral: sessionEphemeral);
        _orchestrator.Script = ["Trả ", "lời."];
        _tokens.Trim = _ => new TrimmedHistoryResult { ConversationSummary = "tóm tắt bị bỏ qua" };

        var command = Command("bí mật của tôi");
        command.Ephemeral = requestEphemeral;

        var streamed = await CollectAsync(Handler().Handle(command, CancellationToken.None));

        // The turn still streams in full...
        Assert.Equal("Trả lời.", string.Concat(streamed));

        using var verify = NewContext();
        // ...but nothing about it is stored.
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
        var session = verify.ChatSessions.AsNoTracking().Single(s => s.Id == _sessionId);
        Assert.True(session.IsEphemeral, "The send must mark the session so the chat list keeps hiding it.");
        Assert.Equal(CreateChatSessionCommand.DefaultChatTitle, session.Title);
        Assert.Null(session.Summary);

        // The 2h history cache IS refreshed, so multi-turn context survives inside the
        // temporary conversation.
        var cached = JsonSerializer.Deserialize<List<HistoryMessage>>((await _cache.GetStringAsync(CacheKey))!)!;
        Assert.Equal(["bí mật của tôi", "Trả lời."], cached.Select(m => m.Content).ToArray());
    }

    // ── regenerate / replace-last-exchange ────────────────────────────────

    [Fact]
    public async Task Regenerate_ReRunsTheLastUserTurn_DropsTrailingReplies_AndStoresNoNewUserRow()
    {
        SeedSession();
        SeedConversation(
            ("user", "câu cũ"), ("assistant", "đáp cũ"),
            ("user", "câu cuối"), ("assistant", "đáp cần thay"));
        _orchestrator.Script = ["đáp mới"];

        var command = Command("nội dung này bị bỏ qua");
        command.Regenerate = true;

        await CollectAsync(Handler().Handle(command, CancellationToken.None));

        // The re-run turn is the stored last user message, not the incoming one.
        Assert.Equal("câu cuối", _orchestrator.LastQuery);
        // ...and it is excluded from the loaded history, exactly like a normal send.
        Assert.Equal(["câu cũ", "đáp cũ"], _tokens.LastInput!.Select(m => m.Content).ToArray());

        using var verify = NewContext();
        var stored = verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == _sessionId).OrderBy(m => m.CreatedAt).ToList();
        Assert.Equal(["câu cũ", "đáp cũ", "câu cuối", "đáp mới"], stored.Select(m => m.Content).ToArray());
        // No duplicate user row was inserted for the regenerate.
        Assert.Equal(2, stored.Count(m => m.Role == "user"));
        Assert.DoesNotContain(stored, m => m.Content == "nội dung này bị bỏ qua");
    }

    [Fact]
    public async Task Regenerate_WithNothingToReRun_YieldsTheNoticeAndTouchesNothing()
    {
        SeedSession();
        var command = Command("bất kỳ");
        command.Regenerate = true;

        var streamed = await CollectAsync(Handler().Handle(command, CancellationToken.None));

        Assert.Equal(["[Không có tin nhắn nào để tạo lại]"], streamed);
        Assert.Equal(0, _orchestrator.Calls);
        using var verify = NewContext();
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
    }

    [Fact]
    public async Task ReplaceLastExchange_DeletesTheLastExchange_ThenSendsTheEditedTurn()
    {
        SeedSession();
        SeedConversation(
            ("user", "câu cũ"), ("assistant", "đáp cũ"),
            ("user", "câu sai"), ("assistant", "đáp cho câu sai"));
        _orchestrator.Script = ["đáp cho câu sửa"];

        var command = Command("câu sửa");
        command.ReplaceLastExchange = true;

        await CollectAsync(Handler().Handle(command, CancellationToken.None));

        Assert.Equal("câu sửa", _orchestrator.LastQuery);
        // The dropped exchange is gone from history; the surviving one remains.
        Assert.Equal(["câu cũ", "đáp cũ"], _tokens.LastInput!.Select(m => m.Content).ToArray());

        using var verify = NewContext();
        var stored = verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == _sessionId).OrderBy(m => m.CreatedAt).ToList();
        Assert.Equal(["câu cũ", "đáp cũ", "câu sửa", "đáp cho câu sửa"], stored.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task ResendModes_InvalidateTheHistoryCacheBeforeRebuilding_SoADeletedExchangeCannotReplay()
    {
        SeedSession();
        SeedConversation(("user", "câu cuối"), ("assistant", "đáp cần thay"));
        await _cache.SetStringAsync(CacheKey, JsonSerializer.Serialize(new List<HistoryMessage>
        {
            new() { Role = "user", Content = "câu cuối", CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
            new() { Role = "assistant", Content = "đáp cần thay", CreatedAt = DateTime.UtcNow.AddMinutes(-1) }
        }));

        var command = Command("bỏ qua");
        command.Regenerate = true;
        await CollectAsync(Handler().Handle(command, CancellationToken.None));

        // The stale snapshot was dropped, so the deleted assistant reply never
        // reappeared in the history handed to the model.
        Assert.DoesNotContain("đáp cần thay", _tokens.LastInput!.Select(m => m.Content));
    }

    // ── marker round-trip on the cancellation path ────────────────────────

    [Fact]
    public async Task CancelledMidStream_PersistsThePartialAnswer_WithItsProtocolMarkersIntact()
    {
        SeedSession();
        // A real answer preceded by status markers the frontend strips for display.
        _orchestrator.Script =
        [
            "[THINKING]: đang suy nghĩ\n",
            "[WEB_SEARCH]: tra cứu\n",
            "[Agent invoked: search]\n",
            "Đây là câu trả lời một phần"
        ];

        await RunUntilCancelledAsync();

        using var verify = NewContext();
        var assistant = Assert.Single(verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == _sessionId && m.Role == "assistant").ToList());

        // Stripping decides only WHETHER to persist; the row keeps the original markers
        // so the history loader can re-extract the thinking steps on reload.
        Assert.Contains("[THINKING]: đang suy nghĩ", assistant.Content);
        Assert.Contains("[WEB_SEARCH]: tra cứu", assistant.Content);
        Assert.Contains("[Agent invoked: search]", assistant.Content);
        Assert.Contains("Đây là câu trả lời một phần", assistant.Content);
    }

    [Fact]
    public async Task CancelledMidStream_WithOnlyProtocolMarkers_PersistsNoAssistantRow()
    {
        SeedSession();
        // Marker-only output strips to nothing, so persisting it would leave a
        // permanently blank bubble in the transcript.
        _orchestrator.Script =
        [
            "[THINKING]: đang suy nghĩ\n",
            "[REASONING]: lập luận\n",
            "[WEB_SEARCH]: tra cứu\n",
            "[Agent invoked: search]\n"
        ];

        await RunUntilCancelledAsync();

        using var verify = NewContext();
        Assert.Empty(verify.ChatMessages.AsNoTracking().Where(m => m.Role == "assistant").ToList());
        // The user's own turn was still committed before streaming began.
        Assert.Single(verify.ChatMessages.AsNoTracking().Where(m => m.Role == "user").ToList());
    }

    [Fact]
    public async Task MidStreamError_DropsTheHistoryCache_SoTheCommittedUserTurnIsNotLostForTwoHours()
    {
        SeedSession();
        await _cache.SetStringAsync(CacheKey, JsonSerializer.Serialize(new List<HistoryMessage>()));
        _orchestrator.Script = ["một phần"];
        _orchestrator.ThrowAfterScript = new InvalidOperationException("model exploded");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectAsync(Handler().Handle(Command("câu hỏi"), CancellationToken.None)));

        // The pre-send snapshot predates the committed user turn; keeping it would hide
        // that turn from the next send for up to two hours.
        Assert.Null(await _cache.GetStringAsync(CacheKey));
        using var verify = NewContext();
        Assert.Single(verify.ChatMessages.AsNoTracking().Where(m => m.Role == "user").ToList());
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private string CacheKey => $"ChatHistory:{_sessionId}";

    private StreamChatCommand Command(string message) => new()
    {
        SessionId = _sessionId,
        TenantId = _tenantId,
        UserId = _userId,
        Message = message
    };

    private StreamChatCommandHandler Handler() => new(
        FakeModelManager(),
        _orchestrator,
        _tokens,
        NewContext(),
        _cache,
        Options.Create(new ChatReasoningOptions()),
        NullLogger<StreamChatCommandHandler>.Instance);

    /// <summary>
    /// A model manager whose chat model is already "loaded": GetChatModelAsync
    /// short-circuits on the cached field, so nothing is read from disk. The handler
    /// only passes the instance to <see cref="ChatHistory"/>, which is what the tests
    /// inspect.
    /// </summary>
    private static LmModelManager FakeModelManager()
    {
        var manager = new LmModelManager(new ConfigurationBuilder().Build());
        typeof(LmModelManager)
            .GetField("_chatModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, RuntimeHelpers.GetUninitializedObject(typeof(LM)));
        return manager;
    }

    /// <summary>
    /// Streams until the orchestrator signals it has finished its script, then cancels
    /// the request the way a client abort does and lets the cancellation propagate —
    /// which is what drives the handler's partial-persistence finally block.
    /// </summary>
    private async Task RunUntilCancelledAsync()
    {
        using var cts = new CancellationTokenSource();
        _orchestrator.CancelAfterScript = cts;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CollectAsync(Handler().Handle(Command("câu hỏi"), cts.Token)));
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var collected = new List<string>();
        await foreach (var chunk in stream) collected.Add(chunk);
        return collected;
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private void SeedSession(string title = "Phiên", bool ephemeral = false)
    {
        _db.ChatSessions.Add(new ChatSession
        {
            Id = _sessionId,
            TenantId = _tenantId,
            UserId = _userId,
            Title = title,
            IsEphemeral = ephemeral
        });
        _db.SaveChanges();
    }

    private void SeedConversation(params (string Role, string Content)[] messages)
    {
        var createdAt = DateTime.UtcNow.AddHours(-1);
        foreach (var (role, content) in messages)
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = _sessionId,
                Role = role,
                Content = content,
                CreatedAt = createdAt
            });
            createdAt = createdAt.AddMinutes(1);
        }
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Model-free orchestrator: records what the handler asked for, replays a script.</summary>
    private sealed class RecordingOrchestrator : IAgentOrchestrator
    {
        public string[] Script { get; set; } = ["ok"];
        public Exception? ThrowAfterScript { get; set; }
        public CancellationTokenSource? CancelAfterScript { get; set; }

        public int Calls { get; private set; }
        public Guid LastTenantId { get; private set; }
        public string? LastUserRole { get; private set; }
        public string? LastQuery { get; private set; }
        public List<(AuthorRole Role, string Text)>? LastHistoryRoles { get; private set; }

        public async IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            ChatHistory history, AgentRequestOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IList<AgentRunStepData>? stepSink = null)
        {
            Calls++;
            LastTenantId = tenantId;
            LastUserRole = userRole;
            LastQuery = query;
            LastHistoryRoles = history.Messages
                .Select(m => (m.AuthorRole, m.Text ?? string.Empty))
                .ToList();

            foreach (var chunk in Script)
            {
                await Task.Yield();
                yield return chunk;
            }

            CancelAfterScript?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowAfterScript is not null) throw ThrowAfterScript;
        }

        public Task<string> ExecuteDirectActionAsync(
            Guid tenantId, Guid userId, string action, string query,
            Guid? approvalId = null, CancellationToken ct = default)
            => throw new NotSupportedException("Chat streaming never executes a direct action.");
    }

    /// <summary>Deterministic stand-in for the token budgeter; captures what it was handed.</summary>
    private sealed class ScriptedTokenManagement : ITokenManagementService
    {
        public Func<List<HistoryMessage>, TrimmedHistoryResult> Trim { get; set; } =
            messages => new TrimmedHistoryResult { Messages = messages };

        public List<HistoryMessage>? LastInput { get; private set; }

        public int EstimateTokenCount(string text) => text.Length / 4;

        public Task<TrimmedHistoryResult> TrimHistoryAsync(
            List<HistoryMessage> messages, int maxTokenBudget, CancellationToken ct = default)
        {
            LastInput = [.. messages];
            return Task.FromResult(Trim(messages));
        }
    }
}
