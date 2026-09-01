using LmKitOmniApi.Services;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>Embeds schema text for indexing/retrieval. Abstracted so the indexing pipeline is unit-testable without a loaded model.</summary>
public interface ISchemaEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Real embedder over the tenant's LM-Kit embedding model, serialized through the
/// same per-model inference lease the RAG pipeline uses.
/// </summary>
public sealed class LmKitSchemaEmbedder : ISchemaEmbedder
{
    private readonly LmModelManager _modelManager;

    public LmKitSchemaEmbedder(LmModelManager modelManager) => _modelManager = modelManager;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var model = await _modelManager.GetEmbeddingModelAsync(ct: ct);
        var embedder = new LMKit.Embeddings.Embedder(model);
        await using (await _modelManager.AcquireEmbeddingInferenceAsync(ct))
            return embedder.GetEmbeddings(text);
    }
}
