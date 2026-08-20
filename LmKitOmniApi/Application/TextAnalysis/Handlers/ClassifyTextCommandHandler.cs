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
        var chatModel = await _modelManager.GetChatModelAsync(ct: cancellationToken);
        await using var inferenceLease = await _modelManager.AcquireChatInferenceAsync(cancellationToken);
        var classifier = new Categorization(chatModel);

        int categoryIndex = classifier.GetBestCategory(request.Categories, request.Text);

        return new ClassifyTextResult
        {
            Category = categoryIndex >= 0 && categoryIndex < request.Categories.Length
                ? request.Categories[categoryIndex]
                : "Unknown",
            Confidence = classifier.Confidence
        };
    }
}
