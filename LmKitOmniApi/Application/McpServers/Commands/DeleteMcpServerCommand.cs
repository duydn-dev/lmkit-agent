using MediatR;

namespace LmKitOmniApi.Application.McpServers.Commands;

/// <summary>Returns true when a row was deleted; false maps to the original empty 404.</summary>
public sealed class DeleteMcpServerCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
}
