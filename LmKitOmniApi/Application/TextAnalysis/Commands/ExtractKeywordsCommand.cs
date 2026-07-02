using MediatR;

namespace LmKitOmniApi.Application.TextAnalysis.Commands;

public class ExtractKeywordsCommand : IRequest<ExtractKeywordsResult>
{
    public string Text { get; set; } = string.Empty;
}

public class ExtractKeywordsResult
{
    public List<string> Keywords { get; set; } = new();
    public float Confidence { get; set; }
}
