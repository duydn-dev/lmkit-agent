using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Local, offline text-to-speech via the Piper CLI (https://github.com/rhasspy/piper) —
/// the default <see cref="ISpeechSynthesizer"/>. Piper runs on-prem, so it preserves the
/// platform's local-first / no-egress posture (unlike a cloud TTS).
///
/// It shells out through the mockable <see cref="IProcessRunner"/> (the same seam the
/// container code-interpreter and browse tool use): text is fed on stdin, Piper writes a
/// complete WAV to a temp <c>--output_file</c>, and we return those bytes. OFF by default —
/// <see cref="IsAvailable"/> is false until <c>Voice:TtsEnabled</c> is true, the executable
/// is configured, and at least one voice model file exists, so a missing/misconfigured
/// engine falls back to the endpoint's "not configured" (501) path instead of throwing.
/// The Piper binary + voice models are provisioned by the operator; execution therefore is
/// only exercisable on a configured host, while arg-building/availability/cleanup are
/// unit-tested against a fake process runner.
/// </summary>
public sealed class PiperSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly IProcessRunner _runner;
    private readonly VoiceOptions _options;
    private readonly ILogger<PiperSpeechSynthesizer> _logger;

    public PiperSpeechSynthesizer(IProcessRunner runner, IOptions<VoiceOptions> options, ILogger<PiperSpeechSynthesizer> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            if (!_options.TtsEnabled) return false;
            if (string.IsNullOrWhiteSpace(_options.PiperExecutablePath)) return false;
            var model = ResolveModelPath(_options.DefaultVoice);
            return model is not null && File.Exists(model);
        }
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.PiperExecutablePath))
            throw new InvalidOperationException("Piper executable path is not configured (Voice:PiperExecutablePath).");

        var model = ResolveModelPath(voice)
            ?? throw new InvalidOperationException("No Piper voice model is configured (Voice:PiperVoices).");
        if (!File.Exists(model))
            throw new InvalidOperationException($"Piper voice model not found: {model}");

        // Defensive length cap (the endpoint validates first, but never trust it blindly
        // before spawning a process).
        if (_options.MaxSynthesisCharacters > 0 && text.Length > _options.MaxSynthesisCharacters)
            text = text[.._options.MaxSynthesisCharacters];

        var outputPath = Path.Combine(Path.GetTempPath(), $"piper-{Guid.NewGuid():N}.wav");
        var arguments = new[] { "--model", model, "--output_file", outputPath };
        try
        {
            var result = await _runner.RunAsync(
                _options.PiperExecutablePath,
                arguments,
                stdin: text,
                timeout: TimeSpan.FromSeconds(Math.Max(1, _options.SynthesisTimeoutSeconds)),
                ct);

            if (result.TimedOut)
                throw new TimeoutException($"Piper synthesis exceeded {_options.SynthesisTimeoutSeconds}s.");
            if (result.ExitCode != 0)
            {
                var err = result.StdErr.Length > 300 ? result.StdErr[..300] : result.StdErr;
                throw new InvalidOperationException($"Piper failed (exit {result.ExitCode}): {err}");
            }
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Piper reported success but produced no audio file.");

            var audio = await File.ReadAllBytesAsync(outputPath, ct);
            if (audio.Length == 0)
                throw new InvalidOperationException("Piper produced an empty audio file.");
            _logger.LogInformation("🔊 [Piper] Synthesized {Bytes} bytes with voice '{Voice}'.", audio.Length, voice);
            return audio;
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Resolves a voice name to a model path: exact match → default voice → any configured.</summary>
    private string? ResolveModelPath(string voice)
    {
        if (!string.IsNullOrWhiteSpace(voice) && _options.PiperVoices.TryGetValue(voice, out var exact) && !string.IsNullOrWhiteSpace(exact))
            return exact;
        if (_options.PiperVoices.TryGetValue(_options.DefaultVoice, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return _options.PiperVoices.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}
