namespace LmKitOmniApi.Domain.Entities;

public class Document
{
    public const string PendingStatus = "Pending";
    public const string ProcessingStatus = "Processing";
    public const string CompletedStatus = "Completed";
    public const string FailedStatus = "Failed";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public bool IsVectorized { get; set; } = false;
    public string VectorizationStatus { get; set; } = PendingStatus;
    public int ProcessingAttempts { get; set; }
    public DateTime? ProcessingLeaseUntilUtc { get; set; }
    public string? LastProcessingError { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}
