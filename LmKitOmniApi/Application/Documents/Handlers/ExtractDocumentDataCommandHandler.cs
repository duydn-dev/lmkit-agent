using MediatR;
using LMKit.Extraction;
using LMKit.Extraction.Ocr;
using LmKitOmniApi.Application.Documents.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class ExtractDocumentDataCommandHandler : IRequestHandler<ExtractDocumentDataCommand, ExtractDocumentDataResult>
{
    private readonly LmModelManager _modelManager;

    public ExtractDocumentDataCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ExtractDocumentDataResult> Handle(ExtractDocumentDataCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.DocumentPath) || !System.IO.File.Exists(request.DocumentPath))
            throw new FileNotFoundException("Document file not found.", request.DocumentPath);

        // Here we use the Vision model because TextExtraction might require OCR for scanned PDFs
        var visionModel = await _modelManager.GetVisionModelAsync();

        var textExtraction = new TextExtraction(visionModel);
        if (!string.IsNullOrEmpty(request.JsonSchema))
        {
            textExtraction.SetElementsFromJsonSchema(request.JsonSchema);
        }

        var ocrEngine = new VlmOcr(visionModel);
        textExtraction.OcrEngine = ocrEngine;

        textExtraction.SetContent(new LMKit.Data.Attachment(request.DocumentPath));
        var result = textExtraction.Parse();

        return new ExtractDocumentDataResult
        {
            JsonData = result.Json
        };
    }
}
