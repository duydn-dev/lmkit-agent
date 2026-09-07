using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Hosted service that runs the real-time voice room agent.
///
/// STRICT NO-OP by default: when <c>Voice:LiveAgentEnabled</c> is false it does nothing.
/// When enabled it mints a LiveKit join token, joins the configured room, and drives the
/// STT → LLM → TTS turn loop, reconnecting on failure until shutdown. It is defensive on
/// every axis — missing config, token/connect failure, or the native LiveKit runtime not
/// being present all log and stand down (or retry) instead of crashing startup. The media
/// loop itself is live-only (needs a LiveKit server + native runtime + a real caller), so it
/// never runs in CI; the turn loop <see cref="RunSessionAsync"/> is unit-tested with fakes.
///
/// ROOM: the room name comes from <see cref="VoiceRoomNaming"/> — the SAME function the
/// browser token endpoint uses — via <see cref="VoiceOptions.TryResolveAgentRoom"/>. It used
/// to join the bare <c>Voice:Room</c> label while the endpoint minted a tenant-scoped name,
/// so the agent and its callers were permanently in different rooms.
///
/// ONE ROOM PER PROCESS (the default): this service is a single background participant, so it
/// occupies exactly ONE room — the one belonging to <c>Voice:AgentTenantId</c> /
/// <c>Voice:AgentUserId</c>. It stands down (loudly) when those are unset instead of joining a
/// room no caller will ever be in.
///
/// MANY ROOMS (opt-in, <c>Voice:DispatcherEnabled=true</c>): the service instead runs
/// <see cref="VoiceRoomDispatcher"/>, which owns one bounded session per room whose owner
/// explicitly asked for an agent through <c>GET /api/speech/token?agent=true</c>. The
/// single-room path above is untouched by that mode and is what runs whenever the switch is
/// off — including when the dispatcher's own configuration is invalid, in which case the
/// service stands down rather than running with a nonsense cap.
///
/// CREDENTIALS: resolved once through <see cref="VoiceLiveKitCredentials"/>, so
/// <c>Voice:LiveKit*</c> and the shared <c>LiveKit:*</c> block configure this agent and the
/// token endpoint identically.
/// </summary>
public sealed class VoiceRoomAgentHostedService : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly VoiceOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceRoomAgentHostedService> _logger;

    public VoiceRoomAgentHostedService(
        IOptions<VoiceOptions> options,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceRoomAgentHostedService> logger)
    {
        _options = options.Value;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LiveAgentEnabled)
        {
            _logger.LogDebug("Voice live room agent disabled (Voice:LiveAgentEnabled=false); hosted service is a no-op.");
            return;
        }

        // ONE credential source shared with SpeechController's token endpoint.
        var credentials = VoiceLiveKitCredentials.Resolve(_options, _configuration);
        if (!credentials.CanJoin)
        {
            _logger.LogWarning(
                "Voice live room agent enabled but LiveKit URL/API key/secret are not configured "
                + "(set Voice:LiveKitUrl/LiveKitApiKey/LiveKitApiSecret or the shared LiveKit:Url/ApiKey/ApiSecret); standing down.");
            return;
        }

        // MANY-ROOM MODE (opt-in). Everything below this branch is the shipped single-room path
        // and runs unchanged whenever Voice:DispatcherEnabled is false.
        if (_options.DispatcherEnabled)
        {
            await RunDispatcherAsync(credentials, stoppingToken);
            return;
        }

        // ONE room-naming function shared with the token endpoint. Without an explicit
        // tenant/user the agent has no room it could usefully occupy — stand down loudly
        // instead of joining a room no caller will ever be in.
        if (!_options.TryResolveAgentRoom(out var roomName, out var roomError))
        {
            _logger.LogWarning(
                "Voice live room agent enabled but its room cannot be resolved: {Reason} "
                + "Rooms are scoped per tenant+user, so the single hosted agent must be told which user's room to join; standing down.",
                roomError);
            return;
        }

        string token;
        try
        {
            token = new AccessToken(credentials.ApiKey, credentials.ApiSecret)
                .WithIdentity(_options.AgentIdentity)
                .WithGrants(new VideoGrants { RoomJoin = true, Room = roomName, CanPublish = true, CanSubscribe = true })
                .WithTtl(TimeSpan.FromHours(6))
                .ToJwt();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mint the LiveKit agent token; standing down.");
            return;
        }

        var room = new VoiceRoomOptions
        {
            Url = credentials.Url,
            Token = token,
            Room = roomName,
            Identity = _options.AgentIdentity,
            Voice = _options.DefaultVoice
        };

        _logger.LogInformation("🎙️ Voice live room agent starting for room '{Room}'.", roomName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await using var session = scope.ServiceProvider.GetRequiredService<ILiveKitMediaSession>();
                var agent = scope.ServiceProvider.GetRequiredService<IVoiceRoomAgent>();
                await RunSessionAsync(session, agent, room, stoppingToken);
                _logger.LogInformation("🎙️ Voice room session ended; will re-join if still running.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🎙️ Voice room session failed; retrying in {Seconds}s.", ReconnectDelay.TotalSeconds);
            }

            try { await Task.Delay(ReconnectDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Many-room mode. Stands down loudly — never crashes the host, never falls back to the
    /// single-room path — when the consent registry is not registered or a cap is nonsense,
    /// because a dispatcher running on guessed limits is worse than no dispatcher.
    /// </summary>
    private async Task RunDispatcherAsync(VoiceLiveKitCredentials credentials, CancellationToken stoppingToken)
    {
        if (!_options.TryValidateDispatcher(out var configError))
        {
            _logger.LogWarning(
                "Voice room dispatcher enabled but its configuration is invalid: {Reason} Standing down; no rooms will be served.",
                configError);
            return;
        }

        // Resolved rather than injected so a missing registration degrades to "stand down"
        // instead of failing DI at startup and taking the whole host with it.
        using var registryScope = _scopeFactory.CreateScope();
        var registry = registryScope.ServiceProvider.GetService<IVoiceAgentConsentRegistry>();
        if (registry is null)
        {
            _logger.LogWarning(
                "Voice room dispatcher enabled but no {Registry} is registered, so no user can opt in and no room "
                + "would ever be joined. Register it in Program.cs (see T2-ROUND4.md); standing down.",
                nameof(IVoiceAgentConsentRegistry));
            return;
        }

        // ONE gate for the whole process: it — not the room count — is what bounds pressure on
        // the single chat/speech inference leases.
        using var turnGate = new SemaphoreSlim(_options.MaxConcurrentTurns, _options.MaxConcurrentTurns);
        var runner = new LiveKitVoiceRoomSessionRunner(
            _options, credentials, _options.ToSessionLimits(), turnGate, _scopeFactory, _logger);
        var dispatcher = new VoiceRoomDispatcher(registry, runner, _options.ToDispatcherLimits(), _logger);

        _logger.LogInformation(
            "🎙️ Voice room dispatcher enabled: serving rooms whose owner opted in via GET /api/speech/token?agent=true.");
        await dispatcher.RunAsync(stoppingToken);
    }

    /// <summary>
    /// The live turn loop: join the room, then for each inbound utterance run one
    /// STT → LLM → TTS turn and publish the spoken reply. Kept static + internal so the
    /// join/loop wiring is concrete and unit-testable with fakes.
    ///
    /// This is the SINGLE-ROOM path and is intentionally left exactly as it shipped — no idle
    /// timeout, no turn budget, no gate. The many-room dispatcher uses
    /// <see cref="VoiceRoomSession"/> instead, where those bounds are required.
    /// </summary>
    internal static async Task RunSessionAsync(
        ILiveKitMediaSession session,
        IVoiceRoomAgent agent,
        VoiceRoomOptions room,
        CancellationToken ct)
    {
        await session.JoinAsync(room, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var utterance = await session.ReadUtteranceAsync(ct);
                if (utterance is null)
                    break; // room closed

                var result = await agent.RunTurnAsync(
                    new VoiceTurnContext { InboundAudio = utterance.Value, Voice = room.Voice },
                    ct);

                if (result.Handled && result.ReplyAudio.Length > 0)
                    await session.PublishAsync(result.ReplyAudio, ct);
            }
        }
        finally
        {
            await session.LeaveAsync(ct);
        }
    }
}
