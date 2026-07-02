using MediatR;

namespace LmKitOmniApi.Application.TextAnalysis.Commands;

public class DetectLanguageCommand : IRequest<DetectLanguageResult>
{
    public string Text { get; set; } = string.Empty;
}

public class DetectLanguageResult
{
    public string Language { get; set; } = string.Empty;
}
