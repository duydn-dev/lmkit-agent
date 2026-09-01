using MediatR;

namespace LmKitOmniApi.Application.McpServers.Commands;

public sealed class UpdateMcpServerCommand : SaveMcpServerCommandBase, IRequest<SaveMcpServerResult>
{
    /// <summary>The server being updated (route id).</summary>
    public Guid ServerId { get; set; }
}
