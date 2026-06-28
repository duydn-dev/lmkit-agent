namespace LmKitOmniApi.Application.Abstractions;

public interface ITextChunkingService
{
    List<string> ChunkText(string text, int maxChunkSize = 1200, int overlap = 200);
}

public interface IRagPipelineService
{
    Task<string> IngestDocumentAsync(Guid tenantId, string fileName, string content);
    Task<string> QueryKnowledgeBaseAsync(Guid tenantId, string query, int topK = 3);
}
