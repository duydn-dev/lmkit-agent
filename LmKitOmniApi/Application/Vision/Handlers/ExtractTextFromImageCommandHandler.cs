using MediatR;
using LMKit.Data;
using LMKit.Extraction.Ocr;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Models;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Vision.Handlers;

public class ExtractTextFromImageCommandHandler : IRequestHandler<ExtractTextFromImageCommand, ExtractTextFromImageResult>
{
    private readonly LmModelManager _modelManager;

    public ExtractTextFromImageCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ExtractTextFromImageResult> Handle(ExtractTextFromImageCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || !System.IO.File.Exists(request.ImagePath))
            throw new FileNotFoundException("Image file not found.", request.ImagePath);

        var visionModel = await _modelManager.GetVisionModelAsync();

        var intent = request.IncludeCoordinates ? VlmOcrIntent.OcrWithCoordinates : VlmOcrIntent.PlainText;
        var ocr = new VlmOcr(visionModel, intent)
        {
            MaximumCompletionTokens = 4096
        };

        var attachment = new Attachment(request.ImagePath);
        var ocrResult = ocr.Run(attachment, 0); // Assuming 1 page image

        var page = ocrResult.PageElement;
        
        var regions = new List<TextRegion>();
        if (request.IncludeCoordinates && page.TextElements != null)
        {
            foreach (var element in page.TextElements)
            {
                regions.Add(new TextRegion
                {
                    Text = element.Text,
                    Left = element.Left,
                    Top = element.Top,
                    Width = element.Width,
                    Height = element.Height
                });
            }
        }

        return new ExtractTextFromImageResult
        {
            Text = page.Text,
            Regions = regions
        };
    }
}
