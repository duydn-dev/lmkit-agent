using LmKitOmniApi.Application.CustomAgents.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.CustomAgents.Handlers;

public class GetCustomAgentsQueryHandler : IRequestHandler<GetCustomAgentsQuery, List<CustomAgentDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetCustomAgentsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<CustomAgentDto>> Handle(GetCustomAgentsQuery request, CancellationToken cancellationToken)
    {
        // Materialize first: the CSV columns are parsed in memory by CustomAgentRules.
        var agents = await _dbContext.CustomAgents
            .AsNoTracking()
            .Where(agent => agent.TenantId == request.TenantId
                && (agent.OwnerUserId == request.UserId || agent.IsSharedWithTenant))
            .OrderByDescending(agent => agent.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return agents
            .Select(agent => CustomAgentRules.ToDto(agent, request.UserId))
            .ToList();
    }
}
