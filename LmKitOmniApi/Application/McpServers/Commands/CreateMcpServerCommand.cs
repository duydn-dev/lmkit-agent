using MediatR;

namespace LmKitOmniApi.Application.McpServers.Commands;

public sealed class CreateMcpServerCommand : SaveMcpServerCommandBase, IRequest<SaveMcpServerResult>
{
}
