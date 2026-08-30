using MediatR;

namespace LmKitOmniApi.Application.Documents.Queries;

public class ListDocumentsQuery : IRequest<List<DocumentListItemDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool IsAdmin { get; set; }

    /// <summary>
    /// When true, return only the caller's OWN documents even for admins. Used by
    /// the custom-agent knowledge picker, whose pinning is validated owner-only —
    /// so the picker must not offer teammates' documents an admin cannot pin.
    /// </summary>
    public bool OwnedOnly { get; set; }
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
