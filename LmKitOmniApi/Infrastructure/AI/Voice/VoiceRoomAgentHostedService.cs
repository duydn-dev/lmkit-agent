using Livekit.Server.Sdk.Dotnet;
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
/// </summary>
public sealed class VoiceRoomAgentHostedService : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly VoiceOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceRoomAgentHostedService> _logger;

    public VoiceRoomAgentHostedService(
        IOptions<VoiceOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceRoomAgentHostedService> logger)
    {
        _options = options.Value;
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

        if (string.IsNullOrWhiteSpace(_options.LiveKitUrl)
            || string.IsNullOrWhiteSpace(_options.LiveKitApiKey)
            || string.IsNullOrWhiteSpace(_options.LiveKitApiSecret))
        {
            _logger.LogWarning("Voice live room agent enabled but LiveKit URL/API key/secret are not configured; standing down.");
            return;
        }

        string token;
        try
        {
            token = new AccessToken(_options.LiveKitApiKey, _options.LiveKitApiSecret)
                .WithIdentity(_options.AgentIdentity)
                .WithGrants(new VideoGrants { RoomJoin = true, Room = _options.Room, CanPublish = true, CanSubscribe = true })
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
            Url = _options.LiveKitUrl,
            Token = token,
            Room = _options.Room,
            Identity = _options.AgentIdentity,
            Voice = _options.DefaultVoice
        };

        _logger.LogInformation("🎙️ Voice live room agent starting for room '{Room}'.", _options.Room);
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
