using MediatR;

namespace LmKitOmniApi.Application.Memory.Commands;

/// <summary>
/// Confirms an inferred memory owned by the tenant/user so it may be recalled
/// into prompts. Returns <c>false</c> when no owned memory matched, which the
/// controller maps to 404.
/// </summary>
public class ConfirmAgentMemoryCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid MemoryId { get; set; }
}
