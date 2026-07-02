using MediatR;
using LMKit.Embeddings;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.TextAnalysis.Handlers;

public class GenerateEmbeddingsCommandHandler : IRequestHandler<GenerateEmbeddingsCommand, GenerateEmbeddingsResult>
{
    private readonly LmModelManager _modelManager;

    public GenerateEmbeddingsCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<GenerateEmbeddingsResult> Handle(GenerateEmbeddingsCommand request, CancellationToken cancellationToken)
    {
        var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
        var embedder = new Embedder(embeddingModel);

        var embeddings = embedder.GetQueryEmbeddings(request.Text);

        return new GenerateEmbeddingsResult
        {
            Embeddings = embeddings
        };
    }
}
