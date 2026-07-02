using MediatR;
using LMKit.TextAnalysis;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.TextAnalysis.Handlers;

public class ExtractKeywordsCommandHandler : IRequestHandler<ExtractKeywordsCommand, ExtractKeywordsResult>
{
    private readonly LmModelManager _modelManager;

    public ExtractKeywordsCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ExtractKeywordsResult> Handle(ExtractKeywordsCommand request, CancellationToken cancellationToken)
    {
        var chatModel = await _modelManager.GetChatModelAsync();
        var extractor = new KeywordExtraction(chatModel);

        return new ExtractKeywordsResult
        {
            Keywords = extractor.ExtractKeywords(request.Text).Select(k => k.Value).ToList(),
            Confidence = extractor.Confidence
        };
    }
}
