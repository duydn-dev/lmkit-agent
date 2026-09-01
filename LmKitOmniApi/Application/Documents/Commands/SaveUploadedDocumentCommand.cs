using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

/// <summary>
/// Persists an already-validated upload: writes the payload into the caller's
/// isolated upload directory and records the document row (status Pending) for
/// the background vectorization job. The controller keeps the IFormFile
/// concerns (size/extension/magic-byte validation) and hands over a plain
/// stream plus metadata.
/// </summary>
public class SaveUploadedDocumentCommand : IRequest<Guid>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Sanitized original file name (already passed through Path.GetFileName).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Lower-cased file extension including the leading dot (e.g. ".pdf").</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>Upload payload. Owned and disposed by the caller.</summary>
    public Stream Content { get; set; } = Stream.Null;
}
