using LmKitOmniApi.Application.CustomAgents.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.CustomAgents.Handlers;

public class GetCustomAgentsQueryHandler : IRequestHandler<GetCustomAgentsQuery, Common.PagedResult<CustomAgentDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetCustomAgentsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Common.PagedResult<CustomAgentDto>> Handle(GetCustomAgentsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.CustomAgents
            .AsNoTracking()
            .Where(agent => agent.TenantId == request.TenantId
                && (agent.OwnerUserId == request.UserId || agent.IsSharedWithTenant));

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(agent => EF.Functions.Like(agent.Name, pattern)
                || (agent.Description != null && EF.Functions.Like(agent.Description, pattern)));
        }

        // Page the ENTITY query, then map in memory: the CSV columns are parsed by
        // CustomAgentRules and cannot be translated to SQL.
        var ordered = query.OrderByDescending(agent => agent.CreatedAtUtc);
        var total = await ordered.CountAsync(cancellationToken);
        var agents = total == 0
            ? []
            : await ordered
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync(cancellationToken);

        return new Common.PagedResult<CustomAgentDto>
        {
            Items = agents.Select(agent => CustomAgentRules.ToDto(agent, request.UserId)).ToList(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = total
        };
    }
}
