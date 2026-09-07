using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// The production <see cref="IVoiceRoomSessionRunner"/>: mints a room-scoped LiveKit token for
/// ONE consented room, builds a fresh DI scope for it, and drives the bounded turn loop.
///
/// LIVE-ONLY BOUNDARY. Everything above it (consent, caps, fairness, reclamation, cancellation)
/// is exercised in CI with a fake runner; what happens below <see cref="ILiveKitMediaSession"/>
/// needs a real LiveKit server and cannot be. The token minted here is scoped to a SINGLE room —
/// <c>VideoGrants.Room = grant.Room</c> — so even if the dispatcher were handed the wrong grant,
/// the credential it carries cannot open a different tenant's room.
/// </summary>
public sealed class LiveKitVoiceRoomSessionRunner : IVoiceRoomSessionRunner
{
    /// <summary>Join tokens are per-session and short-lived; a room outliving this re-joins with a fresh one.</summary>
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(2);

    private readonly VoiceOptions _options;
    private readonly VoiceLiveKitCredentials _credentials;
    private readonly VoiceRoomSessionLimits _limits;
    private readonly SemaphoreSlim _turnGate;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public LiveKitVoiceRoomSessionRunner(
        VoiceOptions options,
        VoiceLiveKitCredentials credentials,
        VoiceRoomSessionLimits limits,
        SemaphoreSlim turnGate,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        _options = options;
        _credentials = credentials;
        _limits = limits;
        _turnGate = turnGate;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync(VoiceAgentGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);

        // The dispatcher checks this too. Re-checked here because this is the method that turns
        // a record into a live credential for a room.
        if (!grant.IsSelfConsistent())
        {
            _logger.LogError(
                "🎙️ Refusing to mint a LiveKit token for room '{Room}': it does not belong to tenant {TenantId}/user {UserId}.",
                grant.Room, grant.TenantId, grant.UserId);
            return;
        }

        string token;
        try
        {
            token = new AccessToken(_credentials.ApiKey, _credentials.ApiSecret)
                .WithIdentity(_options.AgentIdentity)
                .WithGrants(new VideoGrants
                {
                    RoomJoin = true,
                    Room = grant.Room,
                    CanPublish = true,
                    CanSubscribe = true
                })
                .WithTtl(TokenTtl)
                .ToJwt();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🎙️ Failed to mint the LiveKit agent token for room '{Room}'.", grant.Room);
            return;
        }

        var room = new VoiceRoomOptions
        {
            Url = _credentials.Url,
            Token = token,
            Room = grant.Room,
            Identity = _options.AgentIdentity,
            Voice = string.IsNullOrWhiteSpace(grant.Voice) ? _options.DefaultVoice : grant.Voice
        };

        // A scope per ROOM, never one shared across rooms: scoped services (DbContext and
        // anything that will one day carry per-tenant state) must not be reachable from two
        // tenants' sessions at the same time.
        using var scope = _scopeFactory.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<ILiveKitMediaSession>();
        var agent = scope.ServiceProvider.GetRequiredService<IVoiceRoomAgent>();

        var reason = await VoiceRoomSession
            .RunAsync(session, agent, room, grant, _limits, _turnGate, _logger, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("🎙️ Voice room '{Room}' session ended ({Reason}).", grant.Room, reason);
    }
}
