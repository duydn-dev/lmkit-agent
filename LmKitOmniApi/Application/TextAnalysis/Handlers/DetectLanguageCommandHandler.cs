using MediatR;
using LMKit.Translation;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.TextAnalysis.Handlers;

public class DetectLanguageCommandHandler : IRequestHandler<DetectLanguageCommand, DetectLanguageResult>
{
    private readonly LmModelManager _modelManager;

    public DetectLanguageCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<DetectLanguageResult> Handle(DetectLanguageCommand request, CancellationToken cancellationToken)
    {
        var chatModel = await _modelManager.GetChatModelAsync();
        var translator = new TextTranslation(chatModel);

        var language = translator.DetectLanguage(request.Text);

        return new DetectLanguageResult
        {
            Language = language.ToString()
        };
    }
}
