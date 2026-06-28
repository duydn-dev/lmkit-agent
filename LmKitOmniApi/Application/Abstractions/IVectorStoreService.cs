namespace LmKitOmniApi.Application.Abstractions;

public class VectorSearchResult
{
    public Guid Id { get; set; }
    public float Score { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public interface IVectorStoreService
{
    Task UpsertVectorAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object>? payload = null);
    Task<List<VectorSearchResult>> SearchSimilarAsync(string collectionName, float[] queryVector, int topK);
    Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize);

    /// <summary>
    /// H3 Fix: Search by payload keyword filter — independent of vector similarity.
    /// Uses Qdrant's native payload filtering for true sparse retrieval.
    /// </summary>
    Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords, 
        string tenantFilterField, string tenantId, int topK);
}
