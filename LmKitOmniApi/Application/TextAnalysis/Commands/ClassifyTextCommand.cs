using MediatR;

namespace LmKitOmniApi.Application.TextAnalysis.Commands;

public class ClassifyTextCommand : IRequest<ClassifyTextResult>
{
    public string Text { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
}

public class ClassifyTextResult
{
    public string Category { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
