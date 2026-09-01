using MediatR;

namespace LmKitOmniApi.Application.Projects.Commands;

/// <summary>
/// Deletes one of the caller's projects. Returns false when the project does not
/// exist or belongs to another tenant/user (→ 404, never 403). The project's
/// chat sessions survive: the ChatSession→Project FK is configured SetNull, so
/// they simply leave the project.
/// </summary>
public sealed class DeleteProjectCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
}
