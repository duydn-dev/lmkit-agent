using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Infrastructure.AI.Voice;
using MediatR;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Application.Speech.Handlers;

/// <summary>
/// Handles text-to-speech synthesis. Off by default: unless <c>Voice:TtsEnabled</c> is
/// true AND an <see cref="ISpeechSynthesizer"/> is registered and available, it returns
/// <see cref="SynthesizeSpeechStatus.EngineNotConfigured"/> so the controller answers 501.
///
/// The synthesizer is an OPTIONAL dependency (default null): LM-Kit.NET ships no TTS engine,
/// so no implementation is registered in DI by default and the container injects null. This
/// is honest — there is no fake engine — while leaving a clean extension point.
/// </summary>
public sealed class SynthesizeSpeechCommandHandler : IRequestHandler<SynthesizeSpeechCommand, SynthesizeSpeechResult>
{
    private readonly VoiceOptions _options;
    private readonly ISpeechSynthesizer? _synthesizer;

    public SynthesizeSpeechCommandHandler(
        IOptions<VoiceOptions> options,
        ISpeechSynthesizer? synthesizer = null)
    {
        _options = options.Value;
        _synthesizer = synthesizer;
    }

    public async Task<SynthesizeSpeechResult> Handle(SynthesizeSpeechCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text to synthesize must not be empty.", nameof(request));

        if (!_options.TtsEnabled)
            return SynthesizeSpeechResult.NotConfigured(
                "Text-to-speech is disabled. Set Voice:TtsEnabled=true and register a speech-synthesis engine to enable it.");

        if (_synthesizer is null || !_synthesizer.IsAvailable)
            return SynthesizeSpeechResult.NotConfigured(
                "No text-to-speech engine is available. LM-Kit.NET provides speech-to-text only; register an ISpeechSynthesizer implementation to produce audio.");

        var voice = string.IsNullOrWhiteSpace(request.Voice) ? _options.DefaultVoice : request.Voice.Trim();
        var audio = await _synthesizer.SynthesizeAsync(request.Text, voice, cancellationToken);
        return SynthesizeSpeechResult.Success(audio);
    }
}
