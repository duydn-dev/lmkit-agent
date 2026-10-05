using System.ComponentModel.DataAnnotations;

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

    /// <summary>User-assigned category for metadata-aware retrieval (null = none).</summary>
    [MaxLength(100)]
    public string? Category { get; set; }

    /// <summary>User-assigned tags, stored comma-separated; mirrored to the vector payload as an array.</summary>
    [MaxLength(500)]
    public string? Tags { get; set; }

    /// <summary>Provenance tag written to the vector payload (defaults to "upload" for the worker path).</summary>
    [MaxLength(50)]
    public string? Source { get; set; }

    public bool IsVectorized { get; set; } = false;
    public string VectorizationStatus { get; set; } = PendingStatus;
    public int ProcessingAttempts { get; set; }
    public DateTime? ProcessingLeaseUntilUtc { get; set; }
    public string? LastProcessingError { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}
