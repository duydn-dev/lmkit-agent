using System.Net;
using System.Text;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LmKitOmniApi.Tests;

/// <summary>
/// OAuth 2.0 authorization-code (RFC 6749 §4.1 + PKCE RFC 7636) support for MCP servers,
/// proven in CI with no network by reusing the fake-IdP StubHandler/MutableClock pattern from
/// <see cref="McpOAuthTokenProviderTests"/>. Covers: the code→token exchange with PKCE, the
/// refresh-token grant (incl. auto-refresh at expiry), SSRF refusal of internal token
/// endpoints, encryption-at-rest round-trips, single-use/expiring state, and S256 challenge
/// derivation. A public IP literal (8.8.8.8) passes URL validation offline while the stub
/// handler intercepts the POST.
/// </summary>
public sealed class McpOAuthAuthorizationCodeTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    // ── PKCE ──

    [Fact]
    public void Pkce_Challenge_MatchesRfc7636TestVector()
    {
        // RFC 7636 Appendix B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(expected, McpPkce.Challenge(verifier));
    }

    [Fact]
    public void Pkce_Verifier_IsUrlSafeAndUnpadded()
    {
        var verifier = McpPkce.CreateVerifier();

        Assert.Equal(43, verifier.Length); // 32 bytes → 43 base64url chars
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.NotEqual(verifier, McpPkce.CreateVerifier()); // fresh entropy each call
    }

    // ── code → token exchange ──

    [Fact]
    public async Task Exchange_PostsAuthorizationCodeGrantWithPkce_AndPersistsEncryptedToken()
    {
        var (provider, handler, _, _, store) = CreateProvider(_ =>
            Json("{\"access_token\":\"acc-1\",\"refresh_token\":\"ref-1\",\"expires_in\":3600,\"scope\":\"read:tools\"}"));
        var server = Server(store.Protector);
        var userId = Guid.NewGuid();

        await provider.ExchangeAuthorizationCodeAsync(server, server.TenantId, userId, "auth-code-123", "verifier-xyz", "https://app.example/callback");

        Assert.Equal(1, handler.Calls);
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Contains("code=auth-code-123", handler.LastBody);
        Assert.Contains("code_verifier=verifier-xyz", handler.LastBody);
        Assert.Contains("client_secret=s3cr3t", handler.LastBody); // secret was decrypted for the request

        var stored = await store.GetAsync(server.TenantId, userId, server.Id);
        Assert.NotNull(stored);
        Assert.Equal("acc-1", stored!.AccessToken);
        Assert.Equal("ref-1", stored.RefreshToken);
        Assert.Equal("read:tools", stored.Scope);
    }

    [Fact]
    public async Task Exchange_RefusesInternalTokenEndpoint_ViaSsrfGate()
    {
        var (provider, handler, _, _, store) = CreateProvider(_ => Json("{\"access_token\":\"x\"}"));
        var server = Server(store.Protector, tokenUrl: "http://127.0.0.1/token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ExchangeAuthorizationCodeAsync(server, server.TenantId, Guid.NewGuid(), "c", "v", "https://app/cb"));

        Assert.Equal(0, handler.Calls); // never attempted the request
        Assert.Contains("nội bộ", ex.Message); // internal-address denial from the sandbox
    }

    // ── token retrieval / refresh ──

    [Fact]
    public async Task GetUserAccessToken_ReturnsStored_WhenFresh_WithoutNetwork()
    {
        var (provider, handler, clock, _, store) = CreateProvider(_ => throw new InvalidOperationException("network not expected"));
        var server = Server(store.Protector);
        var userId = Guid.NewGuid();
        await store.SaveAsync(server.TenantId, userId, server.Id, "acc-fresh", "ref", clock.GetUtcNow().AddHours(1), "read:tools");

        var token = await provider.GetUserAccessTokenAsync(server, server.TenantId, userId);

        Assert.Equal("acc-fresh", token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetUserAccessToken_AutoRefreshes_WhenExpired_AndPersistsRotatedRefreshToken()
    {
        var (provider, handler, clock, _, store) = CreateProvider(_ =>
            Json("{\"access_token\":\"acc-refreshed\",\"refresh_token\":\"ref-2\",\"expires_in\":3600}"));
        var server = Server(store.Protector);
        var userId = Guid.NewGuid();
        // Already expired, but a refresh token exists.
        await store.SaveAsync(server.TenantId, userId, server.Id, "acc-old", "ref-1", clock.GetUtcNow().AddSeconds(-10), "read:tools");

        var token = await provider.GetUserAccessTokenAsync(server, server.TenantId, userId);

        Assert.Equal("acc-refreshed", token);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
        Assert.Contains("refresh_token=ref-1", handler.LastBody);

        var stored = await store.GetAsync(server.TenantId, userId, server.Id);
        Assert.Equal("acc-refreshed", stored!.AccessToken);
        Assert.Equal("ref-2", stored.RefreshToken); // rotated refresh token persisted
    }

    [Fact]
    public async Task GetUserAccessToken_RefreshesWithinSkew_EvenBeforeHardExpiry()
    {
        var (provider, handler, clock, _, store) = CreateProvider(_ =>
            Json("{\"access_token\":\"acc-2\",\"expires_in\":3600}"));
        var server = Server(store.Protector);
        var userId = Guid.NewGuid();
        // Not expired yet, but within the 30s refresh skew.
        await store.SaveAsync(server.TenantId, userId, server.Id, "acc-1", "ref-1", clock.GetUtcNow().AddSeconds(10), null);

        var token = await provider.GetUserAccessTokenAsync(server, server.TenantId, userId);

        Assert.Equal("acc-2", token);
        Assert.Equal(1, handler.Calls);
        // Refresh token was not rotated by the server → the previous one is kept.
        var stored = await store.GetAsync(server.TenantId, userId, server.Id);
        Assert.Equal("ref-1", stored!.RefreshToken);
    }

    [Fact]
    public async Task GetUserAccessToken_RefusesInternalTokenEndpointOnRefresh_ViaSsrfGate()
    {
        var (provider, handler, clock, _, store) = CreateProvider(_ => Json("{\"access_token\":\"x\"}"));
        var server = Server(store.Protector, tokenUrl: "http://169.254.169.254/token");
        var userId = Guid.NewGuid();
        await store.SaveAsync(server.TenantId, userId, server.Id, "acc-old", "ref-1", clock.GetUtcNow().AddSeconds(-10), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetUserAccessTokenAsync(server, server.TenantId, userId));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetUserAccessToken_Throws_WhenUserNotConnected()
    {
        var (provider, _, _, _, store) = CreateProvider(_ => Json("{\"access_token\":\"x\"}"));
        var server = Server(store.Protector);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetUserAccessTokenAsync(server, server.TenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetUserAccessToken_Throws_WhenExpiredWithoutRefreshToken()
    {
        var (provider, _, clock, _, store) = CreateProvider(_ => Json("{\"access_token\":\"x\"}"));
        var server = Server(store.Protector);
        var userId = Guid.NewGuid();
        await store.SaveAsync(server.TenantId, userId, server.Id, "acc-old", null, clock.GetUtcNow().AddSeconds(-10), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetUserAccessTokenAsync(server, server.TenantId, userId));
    }

    // ── encryption at rest ──

    [Fact]
    public async Task TokenStore_EncryptsTokensAtRest_AndRoundTrips()
    {
        var (_, _, clock, db, store) = CreateProvider(_ => Json("{}"));
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await store.SaveAsync(tenantId, userId, serverId, "plain-access", "plain-refresh", clock.GetUtcNow().AddHours(1), "s");

        var raw = await db.McpUserOAuthTokens.AsNoTracking().SingleAsync(t => t.ServerId == serverId);
        Assert.StartsWith("dp:v1:", raw.AccessTokenProtected);
        Assert.DoesNotContain("plain-access", raw.AccessTokenProtected);
        Assert.NotNull(raw.RefreshTokenProtected);
        Assert.DoesNotContain("plain-refresh", raw.RefreshTokenProtected!);

        var round = await store.GetAsync(tenantId, userId, serverId);
        Assert.Equal("plain-access", round!.AccessToken);
        Assert.Equal("plain-refresh", round.RefreshToken);
    }

    // ── state store (CSRF + replay + expiry) ──

    [Fact]
    public async Task StateStore_CreateThenConsume_ReturnsBinding_AndIsSingleUse()
    {
        var clock = new MutableClock();
        var store = new McpOAuthStateStore(NewMemoryCache(), clock);
        var entry = new McpOAuthStateEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "verifier-abc", "https://app/cb", default);

        var state = await store.CreateAsync(entry);
        var first = await store.ConsumeAsync(state);
        var second = await store.ConsumeAsync(state);

        Assert.NotNull(first);
        Assert.Equal(entry.UserId, first!.UserId);
        Assert.Equal(entry.ServerId, first.ServerId);
        Assert.Equal("verifier-abc", first.CodeVerifier);
        Assert.Null(second); // single-use: a replay finds nothing
    }

    [Fact]
    public async Task StateStore_Consume_RejectsUnknownState()
    {
        var store = new McpOAuthStateStore(NewMemoryCache(), new MutableClock());

        Assert.Null(await store.ConsumeAsync("never-issued"));
        Assert.Null(await store.ConsumeAsync(""));
    }

    [Fact]
    public async Task StateStore_Consume_RejectsExpiredState()
    {
        var clock = new MutableClock();
        var store = new McpOAuthStateStore(NewMemoryCache(), clock);
        var entry = new McpOAuthStateEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "v", "https://app/cb", default);

        var state = await store.CreateAsync(entry);
        clock.Now = clock.Now + McpOAuthStateStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(await store.ConsumeAsync(state));
    }

    // ── per-user bearer application on the invoke path (McpClientService) ──

    [Fact]
    public async Task InvokeTool_AppliesPerUserBearer_ForAuthorizationCodeServer()
    {
        var protector = new McpHeaderProtector(new EphemeralDataProtectionProvider());
        var clock = new MutableClock();
        var db = NewDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var server = new ExternalMcpServer
        {
            TenantId = tenantId,
            Name = "authcode-srv",
            Url = "https://8.8.8.8/mcp",
            AuthMode = "AuthorizationCode",
            OAuthClientId = "client-abc",
            OAuthClientSecretProtected = protector.Protect("s3cr3t"),
            OAuthAuthorizeUrl = "https://8.8.8.8/authorize",
            OAuthTokenUrl = "https://8.8.8.8/token",
            OAuthScopes = "read:tools"
        };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "t" });
        db.ExternalMcpServers.Add(server);
        await db.SaveChangesAsync();

        var store = new McpUserTokenStore(db, protector);
        await store.SaveAsync(tenantId, userId, server.Id, "user-token-123", "ref", clock.GetUtcNow().AddHours(1), "read:tools");

        var sandbox = new ToolSandboxService(NullLogger<ToolSandboxService>.Instance);
        // Fresh token → provider must not hit the network; the stub throws if it does.
        var provider = new McpOAuthTokenProvider(
            new StubHttpClientFactory(new StubHandler(_ => throw new InvalidOperationException("network not expected"))),
            sandbox, protector, store, clock, NullLogger<McpOAuthTokenProvider>.Instance);
        var protocol = new CapturingProtocolClient();
        var mcp = new McpClientService(
            new SingleScopeFactory(db), sandbox, protector, protocol, provider,
            new HttpContextAccessor(), NullLogger<McpClientService>.Instance);
        await mcp.InvalidateTenantCacheAsync(tenantId);

        var result = await mcp.InvokeToolAsync(tenantId, userId, "authcode-srv", "probe",
            new Dictionary<string, object> { ["q"] = "x" });

        Assert.True(result.Success);
        Assert.NotNull(protocol.LastCallHeaders);
        Assert.True(protocol.LastCallHeaders!.TryGetValue("Authorization", out var auth));
        Assert.Equal("Bearer user-token-123", auth);
    }

    [Fact]
    public async Task InvokeTool_DoesNotSucceed_ForAuthorizationCodeServer_WithNoUserInContext()
    {
        var protector = new McpHeaderProtector(new EphemeralDataProtectionProvider());
        var db = NewDbContext();
        var tenantId = Guid.NewGuid();
        var server = new ExternalMcpServer
        {
            TenantId = tenantId,
            Name = "authcode-srv2",
            Url = "https://8.8.8.8/mcp",
            AuthMode = "AuthorizationCode",
            OAuthClientId = "client-abc",
            OAuthClientSecretProtected = protector.Protect("s3cr3t"),
            OAuthAuthorizeUrl = "https://8.8.8.8/authorize",
            OAuthTokenUrl = "https://8.8.8.8/token"
        };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "t" });
        db.ExternalMcpServers.Add(server);
        await db.SaveChangesAsync();

        var sandbox = new ToolSandboxService(NullLogger<ToolSandboxService>.Instance);
        var provider = new McpOAuthTokenProvider(
            new StubHttpClientFactory(new StubHandler(_ => Json("{}"))),
            sandbox, protector, new McpUserTokenStore(db, protector), new MutableClock(),
            NullLogger<McpOAuthTokenProvider>.Instance);
        var mcp = new McpClientService(
            new SingleScopeFactory(db), sandbox, protector, new CapturingProtocolClient(), provider,
            new HttpContextAccessor(), NullLogger<McpClientService>.Instance); // no HttpContext → no ambient user
        await mcp.InvalidateTenantCacheAsync(tenantId);

        // No user in context and none passed → the server cannot be authenticated, so the
        // tool is neither discovered nor invocable.
        var result = await mcp.InvokeToolAsync(tenantId, "authcode-srv2", "probe",
            new Dictionary<string, object> { ["q"] = "x" });

        Assert.False(result.Success);
    }

    // ── helpers ──

    private (McpOAuthTokenProvider provider, StubHandler handler, MutableClock clock, HermesDbContext db, TestTokenStore store)
        CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var clock = new MutableClock();
        var protector = new McpHeaderProtector(new EphemeralDataProtectionProvider());
        var db = NewDbContext();
        var store = new TestTokenStore(db, protector);
        var provider = new McpOAuthTokenProvider(
            new StubHttpClientFactory(handler),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            protector,
            store,
            clock,
            NullLogger<McpOAuthTokenProvider>.Instance);
        return (provider, handler, clock, db, store);
    }

    private HermesDbContext NewDbContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(connection).Options;
        var db = new HermesDbContext(options);
        db.Database.EnsureCreated();
        _disposables.Add(db);
        _disposables.Add(connection);
        return db;
    }

    private static IDistributedCache NewMemoryCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static ExternalMcpServer Server(McpHeaderProtector protector, string tokenUrl = "https://8.8.8.8/token") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "authcode-server",
        AuthMode = "AuthorizationCode",
        OAuthClientId = "client-abc",
        OAuthClientSecretProtected = protector.Protect("s3cr3t"),
        OAuthAuthorizeUrl = "https://8.8.8.8/authorize",
        OAuthTokenUrl = tokenUrl,
        OAuthScopes = "read:tools"
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    /// <summary>Exposes the protector so tests can build matching encrypted fixtures.</summary>
    private sealed class TestTokenStore : IMcpUserTokenStore
    {
        private readonly McpUserTokenStore _inner;
        public McpHeaderProtector Protector { get; }

        public TestTokenStore(HermesDbContext db, McpHeaderProtector protector)
        {
            _inner = new McpUserTokenStore(db, protector);
            Protector = protector;
        }

        public Task<StoredUserToken?> GetAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default)
            => _inner.GetAsync(tenantId, userId, serverId, ct);

        public Task SaveAsync(Guid tenantId, Guid userId, Guid serverId, string accessToken, string? refreshToken, DateTimeOffset expiresAtUtc, string? scope, CancellationToken ct = default)
            => _inner.SaveAsync(tenantId, userId, serverId, accessToken, refreshToken, expiresAtUtc, scope, ct);

        public Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default)
            => _inner.DeleteAsync(tenantId, userId, serverId, ct);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls;
        public string? LastBody;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CapturingProtocolClient : IMcpProtocolClient
    {
        public IReadOnlyDictionary<string, string>? LastCallHeaders;

        private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { q = new { type = "string" } },
            required = new[] { "q" }
        });

        public Task<IReadOnlyList<McpProtocolTool>> ListToolsAsync(Uri endpoint, string serverName, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<McpProtocolTool>>([new McpProtocolTool("probe", "Probe", Schema, IsReadOnly: true)]);

        public Task<McpProtocolCallResult> CallToolAsync(Uri endpoint, string serverName, IReadOnlyDictionary<string, string> headers, string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            LastCallHeaders = headers;
            return Task.FromResult(new McpProtocolCallResult(false, $"{serverName}:{toolName}"));
        }
    }

    private sealed class SingleScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly HermesDbContext _db;
        public SingleScopeFactory(HermesDbContext db) => _db = db;
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(HermesDbContext) ? _db : null;
        public void Dispose() { }
    }
}
