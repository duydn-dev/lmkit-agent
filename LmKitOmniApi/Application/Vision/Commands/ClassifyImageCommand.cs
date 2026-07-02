using MediatR;

namespace LmKitOmniApi.Application.Vision.Commands;

public class ClassifyImageCommand : IRequest<ClassifyImageResult>
{
    public string ImagePath { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
}

public class ClassifyImageResult
{
    public string Category { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
