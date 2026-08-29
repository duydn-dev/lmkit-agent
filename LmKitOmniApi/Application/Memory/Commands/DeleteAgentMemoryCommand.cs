using MediatR;

namespace LmKitOmniApi.Application.Memory.Commands;

/// <summary>
/// Deletes one memory owned by the tenant/user (including its vector
/// representation). Returns <c>false</c> when no owned memory matched, which
/// the controller maps to 404.
/// </summary>
public class DeleteAgentMemoryCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MemoryId { get; set; }
}
