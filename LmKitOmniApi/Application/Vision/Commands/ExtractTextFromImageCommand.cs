using MediatR;
using LmKitOmniApi.Models;

namespace LmKitOmniApi.Application.Vision.Commands;

public class ExtractTextFromImageCommand : IRequest<ExtractTextFromImageResult>
{
    public string ImagePath { get; set; } = string.Empty;
    public bool IncludeCoordinates { get; set; } = false;
}

public class ExtractTextFromImageResult
{
    public string Text { get; set; } = string.Empty;
    public List<TextRegion> Regions { get; set; } = new();
}
