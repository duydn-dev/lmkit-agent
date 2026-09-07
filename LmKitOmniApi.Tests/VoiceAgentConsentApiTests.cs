using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The CONSENT wire contract on <c>GET /api/speech/token</c>, end to end through the real
/// pipeline.
///
/// The rule these hold: a voice room existing is not permission for a server-side agent to join
/// it. Nothing is recorded unless the caller explicitly asks (<c>agent=true</c>), the response
/// reports whether an agent will ACTUALLY join rather than whether one was requested, and a
/// caller can withdraw.
///
/// These hosts configure <c>LiveKit:ApiKey/ApiSecret</c> but deliberately NOT
/// <c>LiveKit:Url</c>: the token endpoint only needs the key pair, while the hosted agent
/// requires a URL and therefore stands down before any LiveKit media code runs. That keeps the
/// whole suite free of the native LiveKit runtime.
/// </summary>
public sealed class VoiceAgentConsentApiTests : IClassFixture<LmKitApiFactory>
{
    private const string ApiKey = "test-livekit-api-key";
    private const string ApiSecret = "test-livekit-api-secret-at-least-32-bytes-long";

    private readonly LmKitApiFactory _factory;

    public VoiceAgentConsentApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Token_WithoutTheAgentFlag_MintsATokenAndRecordsNoConsent()
    {
        using var host = Host(dispatcherEnabled: true, withRegistry: true);
        using var client = await AuthenticateAsync(host);

        var body = await GetTokenAsync(client, "/api/speech/token?room=omni-room");

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.False(body.GetProperty("agent").GetBoolean());
        Assert.Equal(0, Registry(host).Count);   // an unasked-for agent is never scheduled
    }

    [Fact]
    public async Task Token_WithAgentTrue_RecordsConsentForTheCallersOwnRoom()
    {
        using var host = Host(dispatcherEnabled: true, withRegistry: true);
        using var client = await AuthenticateAsync(host);

        var body = await GetTokenAsync(client, "/api/speech/token?room=omni-room&agent=true");

        Assert.True(body.GetProperty("agent").GetBoolean());
        var room = body.GetProperty("room").GetString();

        var grant = Assert.Single(Registry(host).ActiveGrants(DateTimeOffset.UtcNow));
        Assert.Equal(room, grant.Room);
        Assert.Equal(LmKitApiFactory.TenantId, grant.TenantId);
        Assert.Equal(LmKitApiFactory.UserId, grant.UserId);
        Assert.True(grant.IsSelfConsistent());

        // The recorded room is the caller's own, derived from their authenticated identity.
        Assert.True(VoiceRoomNaming.TryParseScopedRoom(grant.Room, out var tenantId, out var userId, out _));
        Assert.Equal(LmKitApiFactory.TenantId, tenantId);
        Assert.Equal(LmKitApiFactory.UserId, userId);
    }

    [Fact]
    public async Task Token_WithAgentTrue_WhileTheDispatcherIsOff_ReportsAgentFalse_AndRecordsNothing()
    {
        using var host = Host(dispatcherEnabled: false, withRegistry: true);
        using var client = await AuthenticateAsync(host);

        var body = await GetTokenAsync(client, "/api/speech/token?room=omni-room&agent=true");

        // Honest: the caller asked, the server cannot serve it, and it says so rather than
        // letting a client display "AI listening" for a room nobody will ever join.
        Assert.False(body.GetProperty("agent").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("agentUnavailableReason").GetString()));
        Assert.Equal(0, Registry(host).Count);
    }

    [Fact]
    public async Task Token_WithAgentTrue_WithoutTheRegistryRegistered_StillMintsAWorkingToken()
    {
        // The degradation that matters if the Program.cs registration is never applied: voice
        // keeps working exactly as it does today and no agent is ever scheduled — no 500.
        using var host = Host(dispatcherEnabled: true, withRegistry: false);
        using var client = await AuthenticateAsync(host);

        var body = await GetTokenAsync(client, "/api/speech/token?room=omni-room&agent=true");

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.False(body.GetProperty("agent").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("agentUnavailableReason").GetString()));
    }

    [Fact]
    public async Task AgentSession_Delete_WithdrawsTheCallersConsent()
    {
        using var host = Host(dispatcherEnabled: true, withRegistry: true);
        using var client = await AuthenticateAsync(host);

        await GetTokenAsync(client, "/api/speech/token?room=omni-room&agent=true");
        Assert.Equal(1, Registry(host).Count);

        var revoked = await client.DeleteAsync("/api/speech/agent-session?room=omni-room");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(0, Registry(host).Count);

        // Idempotent: withdrawing twice is not an error.
        var again = await client.DeleteAsync("/api/speech/agent-session?room=omni-room");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task AgentSession_RejectsAnonymousCallers()
    {
        using var host = Host(dispatcherEnabled: true, withRegistry: true);
        using var anonymous = host.CreateClient();

        var response = await anonymous.DeleteAsync("/api/speech/agent-session?room=omni-room");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_WithAnUnusableRoomLabel_IsRefusedBeforeAnyConsentIsRecorded()
    {
        using var host = Host(dispatcherEnabled: true, withRegistry: true);
        using var client = await AuthenticateAsync(host);

        var response = await client.GetAsync("/api/speech/token?room=%2F%2F%2F&agent=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, Registry(host).Count);
    }

    // ── helpers ──

    private WebApplicationFactory<Program> Host(bool dispatcherEnabled, bool withRegistry) =>
        _factory.WithWebHostBuilder(builder =>
        {
            TestHostConfiguration.Apply(builder, new Dictionary<string, string?>
            {
                // The endpoint needs only the key pair; the hosted agent needs a URL too, so
                // omitting LiveKit:Url keeps the live media stack out of this test host.
                ["LiveKit:ApiKey"] = ApiKey,
                ["LiveKit:ApiSecret"] = ApiSecret,
                ["Voice:LiveAgentEnabled"] = "true",
                ["Voice:DispatcherEnabled"] = dispatcherEnabled ? "true" : "false"
            });

            // Program.cs now registers the consent registry, so the "with" case is simply the
            // shipped wiring and needs nothing added here. The "without" case has to REMOVE it:
            // it exists to prove the endpoint degrades honestly (a working token, agent=false,
            // and a stated reason) on a host where that registration is missing, and omission
            // can no longer produce that host.
            if (!withRegistry)
            {
                builder.ConfigureServices(services =>
                    services.RemoveAll<IVoiceAgentConsentRegistry>());
            }
        });

    private static IVoiceAgentConsentRegistry Registry(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IVoiceAgentConsentRegistry>();

    private static async Task<JsonElement> GetTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpClient> AuthenticateAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
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
