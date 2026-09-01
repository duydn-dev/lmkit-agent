using LmKitOmniApi.Application.Chat.Queries;
using MediatR;

namespace LmKitOmniApi.Application.Projects.Queries;

/// <summary>
/// Lists the chat sessions inside one of the caller's projects, newest first, in
/// the exact <see cref="ChatSessionDto"/> shape of the main sessions list.
/// Returns null when the project does not exist or belongs to another
/// tenant/user (→ 404, never 403).
/// </summary>
public sealed class GetProjectSessionsQuery : IRequest<List<ChatSessionDto>?>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
}
