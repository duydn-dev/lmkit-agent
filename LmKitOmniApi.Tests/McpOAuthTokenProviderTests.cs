using System.Net;
using System.Text;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// OAuth 2.0 client-credentials token provider for MCP servers. Proves the security-
/// relevant behaviours in CI with no network: the client-credentials grant is POSTed and
/// the bearer parsed, tokens are cached and refreshed on expiry, the client secret is
/// decrypted for the request, and an internal token endpoint is refused by the SSRF gate.
/// A public IP literal (8.8.8.8) passes URL validation offline while the stub handler
/// intercepts the actual POST, so no real DNS or socket is used.
/// </summary>
public sealed class McpOAuthTokenProviderTests
{
    [Fact]
    public async Task GetAccessToken_PostsClientCredentialsGrant_AndReturnsBearer()
    {
        var (provider, handler, _, protector) = Create(_ =>
            Json("{\"access_token\":\"tok-abc\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));

        var token = await provider.GetAccessTokenAsync(Server(protector));

        Assert.Equal("tok-abc", token);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("grant_type=client_credentials", handler.LastBody);
        Assert.Contains("client_id=client-123", handler.LastBody);
        Assert.Contains("client_secret=s3cr3t", handler.LastBody); // secret was decrypted for the request
    }

    [Fact]
    public async Task GetAccessToken_IncludesScopes_WhenConfigured()
    {
        var (provider, handler, _, protector) = Create(_ =>
            Json("{\"access_token\":\"tok\",\"expires_in\":3600}"));

        await provider.GetAccessTokenAsync(Server(protector, scopes: "read:tools write:tools"));

        Assert.Contains("scope=read", handler.LastBody);
    }

    [Fact]
    public async Task GetAccessToken_CachesToken_SecondCallDoesNotRefetch()
    {
        var (provider, handler, _, protector) = Create(_ =>
            Json("{\"access_token\":\"tok-abc\",\"expires_in\":3600}"));
        var server = Server(protector);

        var first = await provider.GetAccessTokenAsync(server);
        var second = await provider.GetAccessTokenAsync(server);

        Assert.Equal("tok-abc", first);
        Assert.Equal("tok-abc", second);
        Assert.Equal(1, handler.Calls); // served from cache the second time
    }

    [Fact]
    public async Task GetAccessToken_RefetchesAfterExpiry()
    {
        var current = "tok-1";
        var (provider, handler, clock, protector) = Create(_ =>
            Json($"{{\"access_token\":\"{current}\",\"expires_in\":100}}"));
        var server = Server(protector);

        Assert.Equal("tok-1", await provider.GetAccessTokenAsync(server));

        current = "tok-2";
        clock.Now = clock.Now.AddSeconds(200); // past expiry + skew

        Assert.Equal("tok-2", await provider.GetAccessTokenAsync(server));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetAccessToken_RefusesInternalTokenEndpoint_ViaSsrfGate()
    {
        var (provider, handler, _, protector) = Create(_ => Json("{\"access_token\":\"x\"}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetAccessTokenAsync(Server(protector, tokenUrl: "http://127.0.0.1/token")));

        Assert.Equal(0, handler.Calls); // never even attempted the request
        Assert.Contains("nội bộ", ex.Message); // internal-address denial from the sandbox
    }

    [Fact]
    public async Task GetAccessToken_Throws_WhenServerIsNotClientCredentials()
    {
        var (provider, handler, _, protector) = Create(_ => Json("{\"access_token\":\"x\"}"));
        var server = Server(protector);
        server.AuthMode = "Static";

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(server));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAccessToken_Throws_OnNonSuccessResponse()
    {
        var (provider, _, _, protector) = Create(_ =>
            Json("{\"error\":\"invalid_client\"}", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(Server(protector)));
    }

    [Fact]
    public async Task GetAccessToken_Throws_WhenResponseHasNoAccessToken()
    {
        var (provider, _, _, protector) = Create(_ => Json("{\"token_type\":\"Bearer\"}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(Server(protector)));
    }

    // ── helpers ──

    private static (McpOAuthTokenProvider provider, StubHandler handler, MutableClock clock, McpHeaderProtector protector)
        Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var clock = new MutableClock();
        var protector = new McpHeaderProtector(new EphemeralDataProtectionProvider());
        var provider = new McpOAuthTokenProvider(
            new StubHttpClientFactory(handler),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            protector,
            clock,
            NullLogger<McpOAuthTokenProvider>.Instance);
        return (provider, handler, clock, protector);
    }

    private static ExternalMcpServer Server(McpHeaderProtector protector, string tokenUrl = "https://8.8.8.8/token", string? scopes = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "oauth-server",
        AuthMode = "ClientCredentials",
        OAuthClientId = "client-123",
        OAuthClientSecretProtected = protector.Protect("s3cr3t"),
        OAuthTokenUrl = tokenUrl,
        OAuthScopes = scopes
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
}
