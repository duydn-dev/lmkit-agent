using MediatR;
using LMKit.Document.Conversion;
using LmKitOmniApi.Application.Documents.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class ConvertDocumentCommandHandler : IRequestHandler<ConvertDocumentCommand, ConvertDocumentResult>
{
    private readonly LmModelManager _modelManager;

    public ConvertDocumentCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<ConvertDocumentResult> Handle(ConvertDocumentCommand request, CancellationToken cancellationToken)
    {
        DocumentToMarkdown converter;
        IAsyncDisposable? inferenceLease = null;

        if (request.Strategy.ToLower() == "vlmocr" || request.Strategy.ToLower() == "hybrid")
        {
            var ocrModel = await _modelManager.GetVisionModelAsync(ct: cancellationToken);
            inferenceLease = await _modelManager.AcquireVisionInferenceAsync(cancellationToken);
            converter = new DocumentToMarkdown(ocrModel);
        }
        else
        {
            converter = new DocumentToMarkdown();
        }

        var options = new DocumentToMarkdownOptions();
        if (Enum.TryParse<DocumentToMarkdownStrategy>(request.Strategy, true, out var strategy))
        {
            options.Strategy = strategy;
        }

        try
        {
            var result = converter.Convert(request.FilePath, options);

            return new ConvertDocumentResult
            {
                Markdown = result.Markdown,
                TotalPages = result.Pages.Count,
                Elapsed = result.Elapsed
            };
        }
        finally
        {
            if (inferenceLease is not null)
                await inferenceLease.DisposeAsync();
        }
    }
}
