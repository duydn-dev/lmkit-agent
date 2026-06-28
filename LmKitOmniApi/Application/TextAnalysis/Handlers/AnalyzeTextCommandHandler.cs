using MediatR;
using LMKit.TextAnalysis;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.TextAnalysis.Handlers;

public class AnalyzeTextCommandHandler : IRequestHandler<AnalyzeTextCommand, AnalyzeTextResult>
{
    private readonly LmModelManager _modelManager;

    public AnalyzeTextCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<AnalyzeTextResult> Handle(AnalyzeTextCommand request, CancellationToken cancellationToken)
    {
        var chatModel = await _modelManager.GetChatModelAsync();

        var sentimentEngine = new SentimentAnalysis(chatModel);
        var nerEngine = new NamedEntityRecognition(chatModel);
        var piiEngine = new PiiExtraction(chatModel);

        var sentimentResult = sentimentEngine.GetSentimentCategory(request.Text);
        var nerResult = nerEngine.Recognize(request.Text);
        var piiResult = piiEngine.Extract(request.Text);

        // Redact PII
        string redactedText = request.Text;
        var sortedPii = piiResult.SelectMany(e => e.Occurrences).OrderByDescending(o => o.StartIndex).ToList();
        foreach (var pii in sortedPii)
        {
            int length = pii.EndIndex - pii.StartIndex;
            redactedText = redactedText.Remove(pii.StartIndex, length)
                                       .Insert(pii.StartIndex, new string('*', length));
        }

        return new AnalyzeTextResult
        {
            Sentiment = sentimentResult.ToString(),
            SentimentConfidence = sentimentEngine.Confidence,
            ExtractedEntities = nerResult.Select(e => $"{e.EntityDefinition.Label}: {e.Value}").ToList(),
            RedactedText = redactedText
        };
    }
}
