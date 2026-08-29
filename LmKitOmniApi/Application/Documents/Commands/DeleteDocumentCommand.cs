using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

/// <summary>
/// Deletes a tenant/user-owned document: vectors first, then the file on disk,
/// then the database row (that ordering is intentional and covered by tests).
/// Returns <c>false</c> when the document does not exist or is not visible to
/// the caller (cross-tenant / non-owner), which the controller maps to 404.
/// </summary>
public class DeleteDocumentCommand : IRequest<bool>
{
    public Guid DocumentId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool IsAdmin { get; set; }
}
