using MediatR;
using LMKit.Media.Audio;
using LMKit.Speech;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Speech.Handlers;

public class DetectAudioLanguageCommandHandler : IRequestHandler<DetectAudioLanguageCommand, DetectAudioLanguageResult>
{
    private readonly LmModelManager _modelManager;

    public DetectAudioLanguageCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<DetectAudioLanguageResult> Handle(DetectAudioLanguageCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.AudioPath) || !System.IO.File.Exists(request.AudioPath))
            throw new FileNotFoundException("Audio file not found.", request.AudioPath);

        var speechModel = await _modelManager.GetSpeechModelAsync();
        var engine = new SpeechToText(speechModel);

        var audio = new WaveFile(request.AudioPath);
        var result = engine.DetectLanguage(audio);

        return new DetectAudioLanguageResult
        {
            Language = result.Language,
            Confidence = result.Confidence
        };
    }
}
