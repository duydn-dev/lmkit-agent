namespace LmKitOmniApi.Domain.Entities;

public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Content { get; set; } = string.Empty;
    public int TokenCount { get; set; }
    public Guid VectorId { get; set; } // Map with Qdrant Point ID

    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
}
