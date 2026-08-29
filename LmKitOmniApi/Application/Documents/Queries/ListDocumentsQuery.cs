using MediatR;

namespace LmKitOmniApi.Application.Documents.Queries;

public class ListDocumentsQuery : IRequest<List<DocumentListItemDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool IsAdmin { get; set; }
}

/// <summary>
/// Projection returned by <see cref="ListDocumentsQuery"/>. Property names and
/// declaration order intentionally mirror the previous anonymous-type projection
/// so the serialized JSON shape is unchanged.
/// </summary>
public class DocumentListItemDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public bool IsVectorized { get; set; }
    public string VectorizationStatus { get; set; } = string.Empty;
    public int ProcessingAttempts { get; set; }
    public bool HasError { get; set; }
}
