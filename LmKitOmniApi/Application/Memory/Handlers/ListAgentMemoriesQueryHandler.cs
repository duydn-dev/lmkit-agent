using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Memory.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Memory.Handlers;

public class ListAgentMemoriesQueryHandler : IRequestHandler<ListAgentMemoriesQuery, List<AgentMemoryListItemDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListAgentMemoriesQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<AgentMemoryListItemDto>> Handle(ListAgentMemoriesQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.AgentMemories
            .AsNoTracking()
            .Where(memory => memory.TenantId == request.TenantId
                && memory.UserId == request.UserId
                && (memory.ExpiresAtUtc == null || memory.ExpiresAtUtc > DateTime.UtcNow))
            .OrderByDescending(memory => memory.UpdatedAtUtc)
            .Select(memory => new AgentMemoryListItemDto
            {
                Id = memory.Id,
                MemoryType = memory.MemoryType,
                MemoryKey = memory.MemoryKey,
                MemoryValue = memory.MemoryValue,
                Confidence = memory.Confidence,
                IsConfirmed = memory.IsConfirmed,
                ExpiresAtUtc = memory.ExpiresAtUtc,
                CreatedAtUtc = memory.CreatedAtUtc,
                UpdatedAtUtc = memory.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);
    }
}
