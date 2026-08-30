using MediatR;

namespace LmKitOmniApi.Application.CustomAgents.Commands;

/// <summary>
/// Deletes one of the caller's own custom agents. Returns false (→ 404) when the
/// agent is missing or owned by someone else — never 403. Chat sessions bound to
/// the agent fall back to the default assistant via the FK's SetNull behavior.
/// </summary>
public sealed class DeleteCustomAgentCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid AgentId { get; set; }
}
