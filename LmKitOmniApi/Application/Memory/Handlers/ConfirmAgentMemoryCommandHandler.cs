using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Memory.Commands;

namespace LmKitOmniApi.Application.Memory.Handlers;

public class ConfirmAgentMemoryCommandHandler : IRequestHandler<ConfirmAgentMemoryCommand, bool>
{
    private readonly IAgentMemoryService _memoryService;

    public ConfirmAgentMemoryCommandHandler(IAgentMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    public async Task<bool> Handle(ConfirmAgentMemoryCommand request, CancellationToken cancellationToken)
    {
        return await _memoryService.ConfirmMemoryAsync(request.TenantId, request.UserId, request.MemoryId, cancellationToken);
    }
}
