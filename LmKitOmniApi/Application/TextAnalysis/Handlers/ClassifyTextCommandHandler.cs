using MediatR;
using LMKit.TextAnalysis;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.TextAnalysis.Handlers;

public class ClassifyTextCommandHandler : IRequestHandler<ClassifyTextCommand, ClassifyTextResult>
{
    private readonly LmModelManager _modelManager;

    public ClassifyTextCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ClassifyTextResult> Handle(ClassifyTextCommand request, CancellationToken cancellationToken)
    {
        var chatModel = await _modelManager.GetChatModelAsync();
        var classifier = new Categorization(chatModel);

        int categoryIndex = classifier.GetBestCategory(request.Categories, request.Text);

        return new ClassifyTextResult
        {
            Category = request.Categories[categoryIndex],
            Confidence = classifier.Confidence
        };
    }
}
