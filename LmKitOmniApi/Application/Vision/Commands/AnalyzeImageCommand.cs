using MediatR;

namespace LmKitOmniApi.Application.Vision.Commands;

public class AnalyzeImageCommand : IRequest<string>
{
    public string ImagePath { get; set; } = string.Empty;
    public string Prompt { get; set; } = "What is in this image?";
}
