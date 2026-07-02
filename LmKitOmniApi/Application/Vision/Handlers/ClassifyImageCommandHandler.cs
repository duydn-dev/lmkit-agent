using MediatR;
using LMKit.Media.Image;
using LMKit.TextAnalysis;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Vision.Handlers;

public class ClassifyImageCommandHandler : IRequestHandler<ClassifyImageCommand, ClassifyImageResult>
{
    private readonly LmModelManager _modelManager;

    public ClassifyImageCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ClassifyImageResult> Handle(ClassifyImageCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || !System.IO.File.Exists(request.ImagePath))
            throw new FileNotFoundException("Image file not found.", request.ImagePath);

        var visionModel = await _modelManager.GetVisionModelAsync();
        var classifier = new Categorization(visionModel);

        using var img = ImageBuffer.LoadAsRGB(request.ImagePath);
        int categoryIndex = classifier.GetBestCategory(request.Categories, img);

        return new ClassifyImageResult
        {
            Category = categoryIndex >= 0 && categoryIndex < request.Categories.Length ? request.Categories[categoryIndex] : "Unknown",
            Confidence = classifier.Confidence
        };
    }
}
