using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Data.Interceptors;
using LmKitOmniApi.Infrastructure.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

    [Fact]
    public async Task AiRateLimit_ReturnsContractOnEleventhRequestWithoutInvokingModel()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var invalidCommand = new { sessionId = Guid.Empty, message = "" };

        for (var requestNumber = 1; requestNumber <= 10; requestNumber++)
        {
            var validationResponse = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);
            Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(Assert.Single(retryAfter), out var seconds) && seconds > 0);
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
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public const string Email = "integration@example.test";
    public const string Password = "Integration-2026!";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _sqliteProvider = new ServiceCollection()
        .AddEntityFrameworkSqlite()
        .BuildServiceProvider();
    private readonly object _seedLock = new();
    private bool _seeded;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = "integration-test-secret-key-at-least-32-bytes-long",
                ["JwtSettings:Issuer"] = "LmKitOmniApi",
                ["JwtSettings:Audience"] = "LmKitOmniClient",
                ["JwtSettings:ExpirationInMinutes"] = "30",
                ["AuthCookies:Secure"] = "false",
                ["HttpsRedirection:Enabled"] = "false",
                ["Database:ApplyMigrations"] = "false",
                ["BootstrapAdmin:Enabled"] = "false",
                ["ConnectionStrings:Redis"] = "",
                ["AiModels:WarmupChatModel"] = "false",
                ["AiModels:RequireChatModelReady"] = "false",
                ["RateLimiting:AiRequestsPerWindow"] = "10",
                ["RateLimiting:AiWindowSeconds"] = "3600"
            });
        });
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } implementation
                    && (implementation == typeof(DocumentVectorizationWorker)
                        || implementation == typeof(DataRetentionWorker)
                        || implementation == typeof(ModelWarmupWorker)))
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
                options.UseSqlite(_connection)
                    .UseInternalServiceProvider(_sqliteProvider)
                    .AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>()));
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
        }
    }
}
