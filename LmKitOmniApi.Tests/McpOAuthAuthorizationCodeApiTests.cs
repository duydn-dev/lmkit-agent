using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// End-to-end contract tests for the per-user OAuth 2.0 authorization-code endpoints. Nothing
/// here touches the network: server creation is lazy, the authorize endpoint only builds a URL
/// (public IP literals pass URL validation offline), and the callback rejections are validated
/// before any token exchange is attempted. Proves the client secret and per-user tokens are
/// never returned on any surface.
/// </summary>
public sealed class McpOAuthAuthorizationCodeApiTests : IClassFixture<LmKitApiFactory>
{
    private readonly LmKitApiFactory _factory;

    public McpOAuthAuthorizationCodeApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Create_AuthorizationCode_RoundTrips_ButNeverReturnsTheSecret()
    {
        var client = await LoginAsync();
        var name = $"authc-{Guid.NewGuid():N}".Substring(0, 20);

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name,
            url = "https://8.8.8.8/mcp",
            authMode = "AuthorizationCode",
            oauthClientId = "ac-client-id",
            oauthClientSecret = "super-secret-authcode",
            oauthAuthorizeUrl = "https://8.8.8.8/oauth/authorize",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            oauthScopes = "read:tools",
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var rawBody = await (await client.GetAsync("/api/mcp-servers")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-authcode", rawBody);
        Assert.DoesNotContain("Protected", rawBody, StringComparison.OrdinalIgnoreCase);

        var list = JsonSerializer.Deserialize<JsonElement[]>(rawBody)!;
        var entry = Assert.Single(list, e => e.GetProperty("name").GetString() == name);
        Assert.Equal("AuthorizationCode", entry.GetProperty("authMode").GetString());
        Assert.True(entry.GetProperty("hasOAuthSecret").GetBoolean());
        Assert.Equal("https://8.8.8.8/oauth/authorize", entry.GetProperty("oauthAuthorizeUrl").GetString());
        Assert.Contains("ac-client-id", rawBody); // non-secret config IS returned for edit pre-fill
    }

    [Fact]
    public async Task Create_AuthorizationCode_WithInternalAuthorizeUrl_Returns400_ViaSsrfGate()
    {
        var client = await LoginAsync();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name = $"acssrf-{Guid.NewGuid():N}".Substring(0, 18),
            url = "https://8.8.8.8/mcp",
            authMode = "AuthorizationCode",
            oauthClientId = "ac-client-id",
            oauthClientSecret = "s",
            oauthAuthorizeUrl = "http://127.0.0.1/oauth/authorize", // internal — must be refused
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Create_AuthorizationCode_WithoutSecret_Returns400()
    {
        var client = await LoginAsync();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name = $"acnosec-{Guid.NewGuid():N}".Substring(0, 18),
            url = "https://8.8.8.8/mcp",
            authMode = "AuthorizationCode",
            oauthClientId = "ac-client-id",
            oauthAuthorizeUrl = "https://8.8.8.8/oauth/authorize",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Authorize_ReturnsProviderUrl_WithPkceChallengeAndState()
    {
        var client = await LoginAsync();
        var serverId = await CreateAuthorizationCodeServerAsync(client);

        var response = await client.GetAsync($"/api/mcp-oauth/{serverId}/authorize");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.StartsWith("https://8.8.8.8/oauth/authorize", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=", url);
        Assert.Contains("state=", url);
        Assert.Contains("client_id=ac-client-id", url);
        Assert.Contains("redirect_uri=", url);
    }

    [Fact]
    public async Task Status_ReportsConnection_ButNeverReturnsTheToken()
    {
        var client = await LoginAsync();
        var serverId = await CreateAuthorizationCodeServerAsync(client);

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/mcp-oauth/{serverId}/status");
        Assert.False(before.GetProperty("connected").GetBoolean());

        // Seed an encrypted per-user token directly through the store for the logged-in user.
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMcpUserTokenStore>();
            await store.SaveAsync(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, serverId,
                "seeded-secret-access", "seeded-secret-refresh", DateTimeOffset.UtcNow.AddHours(1), "read:tools");
        }

        var statusResponse = await client.GetAsync($"/api/mcp-oauth/{serverId}/status");
        var rawBody = await statusResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var status = JsonSerializer.Deserialize<JsonElement>(rawBody);
        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.DoesNotContain("seeded-secret-access", rawBody);
        Assert.DoesNotContain("seeded-secret-refresh", rawBody);
    }

    [Fact]
    public async Task Callback_RejectsUnknownState_WithoutTokenExchange()
    {
        // Anonymous by design: the callback validates the state, not a session cookie.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/mcp-oauth/callback?code=some-code&state=never-issued");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString() ?? "");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("expired", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_RejectsMissingCode()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/mcp-oauth/callback?state=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> CreateAuthorizationCodeServerAsync(HttpClient client)
    {
        var name = $"acsrv-{Guid.NewGuid():N}".Substring(0, 20);
        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name,
            url = "https://8.8.8.8/mcp",
            authMode = "AuthorizationCode",
            oauthClientId = "ac-client-id",
            oauthClientSecret = "ac-secret",
            oauthAuthorizeUrl = "https://8.8.8.8/oauth/authorize",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            oauthScopes = "read:tools",
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
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
