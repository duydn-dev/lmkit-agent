using MediatR;

namespace LmKitOmniApi.Application.TextAnalysis.Commands;

public class AnalyzeTextCommand : IRequest<AnalyzeTextResult>
{
    public string Text { get; set; } = string.Empty;
}

public class AnalyzeTextResult
{
    public string Sentiment { get; set; } = string.Empty;
    public float SentimentConfidence { get; set; }
    public List<string> ExtractedEntities { get; set; } = new();
    public string RedactedText { get; set; } = string.Empty;
}
