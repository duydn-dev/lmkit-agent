using MediatR;
using LMKit.Media.Image;
using LMKit.Segmentation;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Vision.Handlers;

public class RemoveBackgroundCommandHandler : IRequestHandler<RemoveBackgroundCommand, RemoveBackgroundResult>
{
    private readonly LmModelManager _modelManager;

    public RemoveBackgroundCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<RemoveBackgroundResult> Handle(RemoveBackgroundCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || !System.IO.File.Exists(request.ImagePath))
            throw new FileNotFoundException("Image file not found.", request.ImagePath);

        var segModel = await _modelManager.GetSegmentationModelAsync();
        var detector = new BackgroundDetection(segModel);

        using var sourceImage = ImageBuffer.LoadAsRGB(request.ImagePath);
        using var resultImage = detector.RemoveBackground(sourceImage);

        // Convert the result to a byte array and then to Base64
        // Since LMKit ImageBuffer SaveAsPng requires a file, we might need a workaround.
        // Wait, ImageBuffer has GetImageBytes or we can save it to a temp file.
        // Let's use a temp file to get the bytes.
        var tempFile = Path.GetTempFileName() + ".png";
        try
        {
            resultImage.SaveAsPng(tempFile);
            var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken);
            return new RemoveBackgroundResult
            {
                Base64Image = Convert.ToBase64String(bytes)
            };
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
