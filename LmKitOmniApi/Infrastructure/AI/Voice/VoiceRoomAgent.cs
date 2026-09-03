using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Skeleton real-time voice room agent. Implements the pure STT → LLM → TTS turn
/// orchestration; every step is an injected seam so the loop is fully unit-testable with
/// fakes and carries no dependency on LiveKit, audio hardware, or a loaded model.
///
/// This type is intentionally NOT registered in DI by default: it needs live STT, LLM and
/// TTS engines that are not wired in this build (LM-Kit ships no TTS at all). The
/// <see cref="VoiceRoomAgentHostedService"/> is the guarded, live-only entry point that would
/// construct and drive it once those engines exist and <c>Voice:LiveAgentEnabled</c> is set.
/// </summary>
public sealed class VoiceRoomAgent : IVoiceRoomAgent
{
    private readonly IVoiceTurnStt _stt;
    private readonly IVoiceTurnLlm _llm;
    private readonly ISpeechSynthesizer _tts;
    private readonly ILogger<VoiceRoomAgent> _logger;

    public VoiceRoomAgent(
        IVoiceTurnStt stt,
        IVoiceTurnLlm llm,
        ISpeechSynthesizer tts,
        ILogger<VoiceRoomAgent> logger)
    {
        _stt = stt;
        _llm = llm;
        _tts = tts;
        _logger = logger;
    }

    public async Task<VoiceTurnResult> RunTurnAsync(VoiceTurnContext context, CancellationToken ct = default)
    {
        // 1) STT — transcribe the inbound utterance.
        var userText = (await _stt.TranscribeAsync(context.InboundAudio, ct))?.Trim() ?? string.Empty;

        // Empty / silent turn: no transcript → do not call the LLM or TTS.
        if (userText.Length == 0)
        {
            _logger.LogDebug("Voice turn skipped: no speech detected in inbound audio.");
            return VoiceTurnResult.Empty("no-speech-detected");
        }

        // 2) LLM — generate the reply text.
        var replyText = (await _llm.RespondAsync(userText, ct))?.Trim() ?? string.Empty;
        if (replyText.Length == 0)
        {
            _logger.LogDebug("Voice turn skipped: language model returned an empty reply.");
            return VoiceTurnResult.Empty("empty-llm-response", userText);
        }

        // 3) TTS — synthesize the spoken reply. If no engine is available we still return the
        // reply text (text-only), never fabricated audio.
        if (!_tts.IsAvailable)
        {
            _logger.LogWarning("Voice turn produced a reply but no TTS engine is available; returning text only.");
            return VoiceTurnResult.Reply(userText, replyText, Array.Empty<byte>());
        }

        var voice = string.IsNullOrWhiteSpace(context.Voice) ? "default" : context.Voice;
        var audio = await _tts.SynthesizeAsync(replyText, voice, ct);
        return VoiceTurnResult.Reply(userText, replyText, audio);
    }
}
