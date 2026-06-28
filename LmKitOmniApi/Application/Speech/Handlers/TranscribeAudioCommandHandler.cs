using MediatR;
using LMKit.Media.Audio;
using LMKit.Speech;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Speech.Handlers;

public class TranscribeAudioCommandHandler : IRequestHandler<TranscribeAudioCommand, TranscribeAudioResult>
{
    private readonly LmModelManager _modelManager;

    public TranscribeAudioCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<TranscribeAudioResult> Handle(TranscribeAudioCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.AudioPath) || !System.IO.File.Exists(request.AudioPath))
            throw new FileNotFoundException("Audio file not found.", request.AudioPath);

        var speechModel = await _modelManager.GetSpeechModelAsync();
        var engine = new SpeechToText(speechModel);

        engine.EnableVoiceActivityDetection = true;

        var audio = new WaveFile(request.AudioPath);
        var result = engine.Transcribe(audio);

        return new TranscribeAudioResult
        {
            Text = result.Text,
            DurationSeconds = audio.Duration.TotalSeconds
        };
    }
}
