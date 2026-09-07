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
/// ONE ROOM PER PROCESS: this service is a single background participant, so it occupies
/// exactly ONE room — the one belonging to <c>Voice:AgentTenantId</c> / <c>Voice:AgentUserId</c>.
/// It stands down (loudly) when those are unset instead of joining a room no caller will ever
/// be in. Serving many users at once requires a room-dispatcher redesign; tracked in
/// <c>LmKitOmniApi/docs/known-issues.md</c>.
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
    /// The live turn loop: join the room, then for each inbound utterance run one
    /// STT → LLM → TTS turn and publish the spoken reply. Kept static + internal so the
    /// join/loop wiring is concrete and unit-testable with fakes.
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
