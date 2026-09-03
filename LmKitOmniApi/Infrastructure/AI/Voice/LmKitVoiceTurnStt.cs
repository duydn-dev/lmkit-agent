using LMKit.Media.Audio;
using LMKit.Speech;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Real speech-to-text step for a voice turn, backed by LM-Kit Whisper — the same engine
/// and single-slot speech-inference lease the batch <c>TranscribeAudioCommandHandler</c>
/// uses. The inbound utterance arrives as complete WAV bytes (already endpointed by the
/// media session), so we write them to a temp file and load them through the proven
/// <see cref="WaveFile"/> path, then delete it. Requires the Whisper model to be loaded, so
/// it only runs on a configured host; the turn-loop that consumes it is unit-tested with a
/// fake instead.
/// </summary>
public sealed class LmKitVoiceTurnStt : IVoiceTurnStt
{
    private readonly LmModelManager _models;
    private readonly ILogger<LmKitVoiceTurnStt> _logger;

    public LmKitVoiceTurnStt(LmModelManager models, ILogger<LmKitVoiceTurnStt> logger)
    {
        _models = models;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        if (audio.IsEmpty) return string.Empty;

        var path = Path.Combine(Path.GetTempPath(), $"voice-stt-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, audio.ToArray(), ct);
        try
        {
            var model = await _models.GetSpeechModelAsync(ct: ct);
            await using var lease = await _models.AcquireSpeechInferenceAsync(ct);
            var engine = new SpeechToText(model) { EnableVoiceActivityDetection = true };
            var result = engine.Transcribe(new WaveFile(path));
            return result.Text?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice STT failed; treating the utterance as silence.");
            return string.Empty;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
