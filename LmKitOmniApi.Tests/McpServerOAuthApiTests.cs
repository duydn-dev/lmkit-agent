using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for OAuth client-credentials on the admin MCP-server endpoints:
/// the mode round-trips, the client secret is accepted but NEVER returned, the token
/// endpoint is SSRF-gated exactly like the server URL, a client-credentials server
/// requires a secret, and switching back to Static clears the stored OAuth config.
/// Public IP literals (8.8.8.8) pass URL validation offline and creation is lazy (no
/// discovery), so nothing here touches the network.
/// </summary>
public sealed class McpServerOAuthApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;

    private readonly LmKitApiFactory _factory;

    public McpServerOAuthApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Create_ClientCredentials_RoundTrips_ButNeverReturnsTheSecret()
    {
        var client = await OwnerClientAsync();
        var name = $"oauth-{Guid.NewGuid():N}".Substring(0, 24);

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name,
            url = "https://8.8.8.8/mcp",
            authMode = "ClientCredentials",
            oauthClientId = "cc-client-id",
            oauthClientSecret = "super-secret-value",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            oauthScopes = "read:tools",
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var listResponse = await client.GetAsync("/api/mcp-servers");
        var rawBody = await listResponse.Content.ReadAsStringAsync();

        // The secret and its encrypted column name must never cross the wire.
        Assert.DoesNotContain("super-secret-value", rawBody);
        Assert.DoesNotContain("Protected", rawBody, StringComparison.OrdinalIgnoreCase);

        var list = JsonSerializer.Deserialize<JsonElement[]>(rawBody)!;
        var entry = Assert.Single(list, e => e.GetProperty("name").GetString() == name);
        Assert.Equal("ClientCredentials", entry.GetProperty("authMode").GetString());
        Assert.True(entry.GetProperty("hasOAuthSecret").GetBoolean());
        Assert.Contains("cc-client-id", rawBody); // non-secret config IS returned (for edit pre-fill)
    }

    [Fact]
    public async Task Create_ClientCredentials_WithoutSecret_Returns400()
    {
        var client = await OwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name = $"noSecret-{Guid.NewGuid():N}".Substring(0, 20),
            url = "https://8.8.8.8/mcp",
            authMode = "ClientCredentials",
            oauthClientId = "cc-client-id",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Create_ClientCredentials_WithInternalTokenUrl_Returns400_ViaSsrfGate()
    {
        var client = await OwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name = $"ssrf-{Guid.NewGuid():N}".Substring(0, 18),
            url = "https://8.8.8.8/mcp",
            authMode = "ClientCredentials",
            oauthClientId = "cc-client-id",
            oauthClientSecret = "s",
            oauthTokenUrl = "http://127.0.0.1/oauth/token", // internal — must be refused
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Update_BackToStatic_ClearsStoredOAuthConfig()
    {
        var client = await OwnerClientAsync();
        var name = $"switch-{Guid.NewGuid():N}".Substring(0, 22);

        var create = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            name,
            url = "https://8.8.8.8/mcp",
            authMode = "ClientCredentials",
            oauthClientId = "cc-client-id",
            oauthClientSecret = "will-be-cleared",
            oauthTokenUrl = "https://8.8.8.8/oauth/token",
            isActive = true
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var update = await client.PutAsJsonAsync($"/api/mcp-servers/{id}", new
        {
            name,
            url = "https://8.8.8.8/mcp",
            authMode = "Static",
            replaceHeaders = false,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/mcp-servers");
        var entry = list!.Single(e => e.GetProperty("name").GetString() == name);
        Assert.Equal("Static", entry.GetProperty("authMode").GetString());
        Assert.False(entry.GetProperty("hasOAuthSecret").GetBoolean());
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
        finally { ClientGate.Release(); }
    }

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
