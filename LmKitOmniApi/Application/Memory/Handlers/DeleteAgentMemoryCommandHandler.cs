using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Memory.Commands;

namespace LmKitOmniApi.Application.Memory.Handlers;

public class DeleteAgentMemoryCommandHandler : IRequestHandler<DeleteAgentMemoryCommand, bool>
{
    private readonly IAgentMemoryService _memoryService;

    public DeleteAgentMemoryCommandHandler(IAgentMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    public async Task<bool> Handle(DeleteAgentMemoryCommand request, CancellationToken cancellationToken)
    {
        return await _memoryService.DeleteMemoryAsync(request.TenantId, request.UserId, request.MemoryId, cancellationToken);
    }
}
