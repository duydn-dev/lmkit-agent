using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Data.Interceptors;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Tests;

public sealed class ApiIntegrationTests : IClassFixture<LmKitApiFactory>
{
    private static readonly Guid OwnMemoryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherMemoryId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OwnApprovalId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OtherApprovalId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private readonly LmKitApiFactory _factory;

    public ApiIntegrationTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task McpInvocation_IsBoundToTheDiscoveredServerWhenToolNamesCollide()
    {
        using var scope = _factory.Services.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<McpClientService>();
        await mcp.InvalidateTenantCacheAsync(LmKitApiFactory.TenantId);

        var discovered = await mcp.DiscoverToolsAsync(LmKitApiFactory.TenantId);
        var result = await mcp.InvokeToolAsync(
            LmKitApiFactory.TenantId,
            "mcp-beta",
            "lookup",
            new Dictionary<string, object> { ["query"] = "tenant-safe" });

        Assert.Equal(2, discovered.Count(tool => tool.Name == "lookup"));
        Assert.Contains(discovered, tool => tool.ServerName == "mcp-alpha" && !tool.AllowAutomaticExecution);
        Assert.Contains(discovered, tool => tool.ServerName == "mcp-beta" && tool.AllowAutomaticExecution);
        Assert.True(result.Success);
        Assert.Equal("mcp-beta:lookup", result.Content);
    }

    [Fact]
    public async Task MemoryEndpoint_RejectsAnonymousRequest()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/memory");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginCookie_AuthenticatesMeEndpoint()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LmKitApiFactory.UserId, body.GetProperty("id").GetGuid());
        Assert.Equal(LmKitApiFactory.TenantId, body.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task ChatMessages_DistinguishesOwnedEmptySessionFromMissingOrCrossTenantSession()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var owned = await client.GetAsync("/api/chat/sessions/55555555-5555-5555-5555-555555555555/messages");
        var otherTenant = await client.GetAsync("/api/chat/sessions/66666666-6666-6666-6666-666666666666/messages");
        var missing = await client.GetAsync($"/api/chat/sessions/{Guid.NewGuid()}/messages");
        var ownedBody = await owned.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        Assert.Empty(ownedBody!);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task MemoryConfirmation_IsOwnerScopedAndUpdatesConsentState()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var crossTenant = await client.PostAsync($"/api/memory/{OtherMemoryId}/confirm", null);
        var own = await client.PostAsync($"/api/memory/{OwnMemoryId}/confirm", null);
        var memories = await client.GetFromJsonAsync<JsonElement[]>("/api/memory");

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        var confirmed = Assert.Single(memories!, item => item.GetProperty("id").GetGuid() == OwnMemoryId);
        Assert.True(confirmed.GetProperty("isConfirmed").GetBoolean());
    }

    /// <summary>
    /// Eleven requests against the host's budget of ten: the eleventh is refused without
    /// reaching the model, and the refusal advertises the CONFIGURED window.
    ///
    /// <para>That last clause is new, and it is the point. This test host asks for a
    /// 3600-second window; until the factory's settings became host configuration the
    /// limiter ran on the appsettings default of 60 instead, and the old assertion
    /// (<c>seconds &gt; 0</c>) could not tell the two apart — it passed on 60 while
    /// claiming to have proved something about 3600. Asserting the exact value is what
    /// makes an inert override visible.</para>
    /// </summary>
    [Fact]
    public async Task AiRateLimit_ReturnsContractOnEleventhRequestWithoutInvokingModel()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var invalidCommand = new { sessionId = Guid.Empty, message = "" };

        for (var requestNumber = 1; requestNumber <= LmKitApiFactory.AiRequestsPerWindow; requestNumber++)
        {
            var validationResponse = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);
            Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(Assert.Single(retryAfter), out var seconds));
        Assert.Equal(LmKitApiFactory.AiWindowSeconds, seconds);
    }

    [Fact]
    public async Task ChatAttachment_IsDeletedAfterRequestProcessing()
    {
        using var isolatedFactory = new LmKitApiFactory();
        isolatedFactory.EnsureSeeded();
        using var client = await CreateAuthenticatedClientAsync(isolatedFactory);
        var attachmentDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Uploads",
            LmKitApiFactory.TenantId.ToString("N"),
            LmKitApiFactory.UserId.ToString("N"),
            "ChatAttachments");
        var filesBefore = Directory.Exists(attachmentDirectory)
            ? Directory.GetFiles(attachmentDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var request = new MultipartFormDataContent();
        request.Add(new StringContent("55555555-5555-5555-5555-555555555555"), "sessionId");
        request.Add(new StringContent("Tóm tắt file này"), "message");
        request.Add(new StringContent("false"), "saveToKnowledge");
        request.Add(new StringContent("Nội dung kiểm tra cleanup file tạm."), "files", "cleanup-check.txt");

        var response = await client.PostAsync("/api/chat/stream-with-files", request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var filesAfter = Directory.Exists(attachmentDirectory)
            ? Directory.GetFiles(attachmentDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(filesBefore.SetEquals(filesAfter), "Temporary chat attachment was not deleted.");
        Assert.Contains("cleanup-check.txt", responseBody);
        Assert.Contains("[DONE]", responseBody);
    }

    [Fact]
    public async Task ChatAttachments_RejectsTooManyFilesBeforeWritingScratchData()
    {
        using var isolatedFactory = new LmKitApiFactory();
        isolatedFactory.EnsureSeeded();
        using var client = await CreateAuthenticatedClientAsync(isolatedFactory);
        var attachmentDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Uploads",
            LmKitApiFactory.TenantId.ToString("N"),
            LmKitApiFactory.UserId.ToString("N"),
            "ChatAttachments");
        var filesBefore = Directory.Exists(attachmentDirectory)
            ? Directory.GetFiles(attachmentDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var request = new MultipartFormDataContent();
        request.Add(new StringContent("55555555-5555-5555-5555-555555555555"), "sessionId");
        request.Add(new StringContent("Tóm tắt các file"), "message");
        request.Add(new StringContent("false"), "saveToKnowledge");
        for (var index = 0; index < 9; index++)
            request.Add(new StringContent($"Nội dung {index}"), "files", $"batch-{index}.txt");

        var response = await client.PostAsync("/api/chat/stream-with-files", request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var filesAfter = Directory.Exists(attachmentDirectory)
            ? Directory.GetFiles(attachmentDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("maximum of 8 attachments", responseBody);
        Assert.True(filesBefore.SetEquals(filesAfter), "Rejected attachment batch wrote scratch files.");
    }

    [Fact]
    public async Task RejectApproval_IsOwnerScopedAndCanResolveOnlyOnce()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var rejection = new { comment = "Integration rejection" };

        var crossTenant = await client.PostAsJsonAsync(
            $"/api/TaskApproval/{OtherApprovalId}/reject",
            rejection);
        var own = await client.PostAsJsonAsync(
            $"/api/TaskApproval/{OwnApprovalId}/reject",
            rejection);
        var repeated = await client.PostAsJsonAsync(
            $"/api/TaskApproval/{OwnApprovalId}/reject",
            rejection);
        var body = await own.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var approval = await db.TaskApprovals.AsNoTracking().SingleAsync(item => item.Id == OwnApprovalId);
        Assert.Equal("Rejected", approval.Status);
        Assert.Equal("Integration rejection", approval.RejectionComment);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
        => await CreateAuthenticatedClientAsync(_factory);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(LmKitApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = LmKitApiFactory.Email,
            password = LmKitApiFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}

public sealed class LmKitApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Extra configuration layered on top of the shared test settings, for a one-off
    /// host (e.g. a tighter rate-limit window). Populate it BEFORE touching
    /// <c>Services</c>/<c>CreateClient</c> — the host is built lazily on first use, and a
    /// later write throws rather than being ignored. Kept as a property rather than a
    /// constructor argument: xunit class fixtures require this type to have exactly one
    /// public constructor. See <see cref="TestHostConfiguration"/> for why these reach
    /// <c>Program.cs</c>'s top-level statements at all.
    /// </summary>
    public TestHostConfigurationOverrides ConfigurationOverrides { get; } = new();

    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public const string Email = "integration@example.test";
    public const string Password = "Integration-2026!";

    /// <summary>
    /// The "ai-agent" budget this host actually runs on. Exposed so a test asserting on the
    /// limiter states the same numbers the host was configured with instead of repeating a
    /// literal that can quietly stop matching — which is precisely how the window came to
    /// be asserted as "some positive number" while the host ran on the appsettings default.
    /// </summary>
    public const int AiRequestsPerWindow = 10;

    /// <inheritdoc cref="AiRequestsPerWindow"/>
    public const int AiWindowSeconds = 3600;

    /// <summary>
    /// Every host gets its OWN data-protection key ring. Program.cs reads
    /// <c>DataProtection:KeyPath</c> in a top-level statement and defaults it to
    /// <c>&lt;content root&gt;/App_Data/DataProtectionKeys</c> — one directory shared by every
    /// factory in the process. Under real parallelism several hosts create and read that key
    /// ring at the same time, and a host that reads a half-written key XML fails its first
    /// protected operation, which surfaces as a 500 on login and takes the whole fixture with
    /// it. Reproduced at <c>xUnit.MaxParallelThreads=32</c>.
    ///
    /// The path travels as host configuration, which is the only layer the entry point's
    /// top-level statements can see — see <see cref="TestHostConfiguration"/>, which is now
    /// how EVERY setting on this host is written, including
    /// <see cref="ConfigurationOverrides"/>.
    /// </summary>
    private readonly string _dataProtectionKeyPath =
        Path.Combine(Path.GetTempPath(), $"lmkit-tests-dpkeys-{Guid.NewGuid():N}");

    /// <summary>
    /// A NAMED shared-cache in-memory database, not a single shared <see cref="SqliteConnection"/>
    /// handed to <c>UseSqlite</c>. EF constructs a <c>SqliteRelationalConnection</c> per DbContext
    /// and its constructor calls <c>SqliteConnection.CreateFunction</c>, which writes to a plain
    /// non-thread-safe <c>Dictionary</c> on the connection object. Two scoped DbContexts built at
    /// the same time — which any test issuing concurrent HTTP calls does — corrupt it:
    /// "Operations that change non-concurrent collections must have exclusive access", thrown from
    /// DI resolution and therefore taking down whole fixtures. Reproduced at
    /// <c>xUnit.MaxParallelThreads=32</c>. With a connection string each DbContext gets its own
    /// connection; <see cref="_connection"/> stays open only to keep the in-memory database alive
    /// for the lifetime of the host.
    /// </summary>
    private readonly string _databaseName = $"lmkit-tests-{Guid.NewGuid():N}";

    private string ConnectionString => $"Data Source={_databaseName};Mode=Memory;Cache=Shared";

    private readonly SqliteConnection _connection;

    public LmKitApiFactory() => _connection = new SqliteConnection(ConnectionString);
    private readonly ServiceProvider _sqliteProvider = new ServiceCollection()
        .AddEntityFrameworkSqlite()
        .BuildServiceProvider();
    private readonly object _seedLock = new();
    private bool _seeded;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = TestHostConfiguration.SharedDefaults();
        settings["DataProtection:KeyPath"] = _dataProtectionKeyPath;
        // These two are read by Program.cs BEFORE builder.Build() and are the reason the
        // seam matters: while they were layered as app configuration the window stayed at
        // the appsettings default of 60s no matter what this said — measured through the
        // 429's Retry-After header, which reported 60, not 3600. Now that 3600 is real the
        // bucket does not refill during a run: a host's AI budget is 10 requests for the
        // whole class, so a test that needs more than that must build a host of its own.
        settings["RateLimiting:AiRequestsPerWindow"] = AiRequestsPerWindow.ToString(CultureInfo.InvariantCulture);
        settings["RateLimiting:AiWindowSeconds"] = AiWindowSeconds.ToString(CultureInfo.InvariantCulture);
        // Same reasoning for the widget key exchange: the throttling test spins up its own
        // host with a tight window.
        settings["RateLimiting:WidgetAuthRequestsPerWindow"] = "500";
        settings["RateLimiting:WidgetAuthWindowSeconds"] = "3600";

        foreach (var (key, value) in ConfigurationOverrides.Consume())
            settings[key] = value;

        TestHostConfiguration.Apply(builder, settings);
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } implementation
                    && (implementation == typeof(DocumentVectorizationWorker)
                        || implementation == typeof(DataRetentionWorker)
                        || implementation == typeof(ModelWarmupWorker)
                        || implementation == typeof(SchemaVectorizationWorker)))
                .ToList())
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<HermesDbContext>();
            services.RemoveAll<DbContextOptions<HermesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<HermesDbContext>>();
            _connection.Open();
            services.AddSingleton(_connection);
            services.AddDbContext<HermesDbContext>((provider, options) =>
                options.UseSqlite(ConnectionString)
                    .UseInternalServiceProvider(_sqliteProvider)
                    .AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>()));
            services.RemoveAll<IMcpProtocolClient>();
            services.AddSingleton<IMcpProtocolClient, TestMcpProtocolClient>();

            // The public-widget integration tests run the REAL WidgetChatEngine — only
            // its LM boundary (model load + native Submit) is canned, so the engine's
            // token subscription, channel drain and guardrail are covered end to end.
            services.AddSingleton<LmKitOmniApi.Application.Widget.IWidgetInferenceSessionFactory,
                TestWidgetInferenceSessionFactory>();
            services.RemoveAll<LmKitOmniApi.Application.Widget.IWidgetChatEngine>();
            services.AddScoped<LmKitOmniApi.Application.Widget.IWidgetChatEngine>(provider =>
                new LmKitOmniApi.Application.Widget.WidgetChatEngine(
                    provider.GetRequiredService<LmKitOmniApi.Application.Widget.IWidgetInferenceSessionFactory>(),
                    provider.GetRequiredService<LmKitOmniApi.Infrastructure.AI.Filters.OutputGuardrailFilter>(),
                    provider.GetRequiredService<ILogger<LmKitOmniApi.Application.Widget.WidgetChatEngine>>()));

            // The "widget-auth" rate-limit policy that POST /api/widget/auth requires is now
            // registered by Program.cs, so the test host no longer mirrors it here.
        });
    }

    public void EnsureSeeded()
    {
        lock (_seedLock)
        {
            if (_seeded) return;
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            db.Database.EnsureCreated();

            var otherTenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var otherUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            db.Tenants.AddRange(
                new Tenant { Id = TenantId, Name = "Integration tenant" },
                new Tenant { Id = otherTenantId, Name = "Other tenant" });
            db.Users.AddRange(
                new User
                {
                    Id = UserId,
                    TenantId = TenantId,
                    Username = "integration",
                    Email = Email,
                    FullName = "Integration User",
                    Role = "Admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password)
                },
                new User
                {
                    Id = otherUserId,
                    TenantId = otherTenantId,
                    Username = "other",
                    Email = "other@example.test",
                    FullName = "Other User",
                    Role = "Admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Other-2026!")
                });
            var ownSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var otherSessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            db.ChatSessions.AddRange(
                new ChatSession { Id = ownSessionId, TenantId = TenantId, UserId = UserId, Title = "Own session" },
                new ChatSession { Id = otherSessionId, TenantId = otherTenantId, UserId = otherUserId, Title = "Other session" });
            db.AgentMemories.AddRange(
                new AgentMemory
                {
                    Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    TenantId = TenantId,
                    UserId = UserId,
                    MemoryType = "Preference",
                    MemoryKey = "own",
                    MemoryValue = "Own pending memory",
                    IsConfirmed = false
                },
                new AgentMemory
                {
                    Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    TenantId = otherTenantId,
                    UserId = otherUserId,
                    MemoryType = "Preference",
                    MemoryKey = "other",
                    MemoryValue = "Other tenant memory",
                    IsConfirmed = false
                });
            db.TaskApprovals.AddRange(
                new TaskApproval
                {
                    Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    TenantId = TenantId,
                    UserId = UserId,
                    ChatSessionId = ownSessionId,
                    ActionName = "TEST_ACTION",
                    ParametersJson = "unused"
                },
                new TaskApproval
                {
                    Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                    TenantId = otherTenantId,
                    UserId = otherUserId,
                    ChatSessionId = otherSessionId,
                    ActionName = "TEST_ACTION",
                    ParametersJson = "unused"
                });
            db.ExternalMcpServers.AddRange(
                new ExternalMcpServer
                {
                    TenantId = TenantId,
                    Name = "mcp-alpha",
                    Url = "https://203.0.113.10/mcp-alpha"
                },
                new ExternalMcpServer
                {
                    TenantId = TenantId,
                    Name = "mcp-beta",
                    Url = "https://203.0.113.10/mcp-beta",
                    TrustReadOnlyAnnotations = true
                });
            db.SaveChanges();
            _seeded = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            _sqliteProvider.Dispose();
            try { Directory.Delete(_dataProtectionKeyPath, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { /* best effort cleanup */ }
        }
    }
}

public sealed class TestMcpProtocolClient : IMcpProtocolClient
{
    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { query = new { type = "string" } },
        required = new[] { "query" },
        additionalProperties = false
    });

    public Task<IReadOnlyList<McpProtocolTool>> ListToolsAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<McpProtocolTool>>(
            [new McpProtocolTool("lookup", "Lookup", InputSchema, IsReadOnly: true)]);

    public Task<McpProtocolCallResult> CallToolAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct) => Task.FromResult(new McpProtocolCallResult(false, $"{serverName}:{toolName}"));
}

/// <summary>
/// Canned LM boundary for the widget engine. Only the model call is faked: the
/// REAL <see cref="LmKitOmniApi.Application.Widget.WidgetChatEngine"/> runs on top
/// of it, so the widget integration tests cover its channel plumbing — the token
/// subscription, the drain and the guardrail. (Substituting the whole engine is
/// what let a missing AfterTextCompletion subscription ship: every widget answer
/// was empty and the endpoint always returned the canned apology.)
/// </summary>
public sealed class TestWidgetInferenceSessionFactory : LmKitOmniApi.Application.Widget.IWidgetInferenceSessionFactory
{
    public const string CannedAnswer = "Canned widget answer";

    /// <summary>The canned answer, split so the test proves segments are concatenated.</summary>
    private static readonly string[] Segments = ["Canned ", "widget ", "answer"];

    public ValueTask<LmKitOmniApi.Application.Widget.IWidgetInferenceSession> OpenAsync(
        LmKitOmniApi.Application.Widget.WidgetTurnRequest request,
        CancellationToken ct)
        => ValueTask.FromResult<LmKitOmniApi.Application.Widget.IWidgetInferenceSession>(new Session());

    private sealed class Session : LmKitOmniApi.Application.Widget.IWidgetInferenceSession
    {
        public event EventHandler<LmKitOmniApi.Application.Widget.WidgetTextSegmentEventArgs>? AfterTextCompletion;

        /// <summary>Mirrors the native call: raises each segment synchronously, then returns.</summary>
        public void Submit(string message, CancellationToken ct)
        {
            foreach (var segment in Segments)
            {
                ct.ThrowIfCancellationRequested();
                AfterTextCompletion?.Invoke(this, new LmKitOmniApi.Application.Widget.WidgetTextSegmentEventArgs(
                    LMKit.TextGeneration.Chat.TextSegmentType.UserVisible, segment));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
