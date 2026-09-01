using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Audit.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Audit.Handlers;

public sealed class GetAuditFacetsQueryHandler : IRequestHandler<GetAuditFacetsQuery, AuditFacetsDto>
{
    // Entity types (tool names) can be high-cardinality; cap the dropdown so a
    // busy tenant does not ship thousands of options to the browser.
    private const int MaxEntityTypes = 100;

    private readonly HermesDbContext _dbContext;

    public GetAuditFacetsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AuditFacetsDto> Handle(GetAuditFacetsQuery request, CancellationToken cancellationToken)
    {
        var scoped = _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => log.TenantId == request.TenantId);

        var actorTypes = await scoped
            .Select(log => log.ActorType)
            .Where(value => value != "")
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

        var actions = await scoped
            .Select(log => log.Action)
            .Where(value => value != "")
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

        var entityTypes = await scoped
            .Select(log => log.EntityType)
            .Where(value => value != "")
            .Distinct()
            .OrderBy(value => value)
            .Take(MaxEntityTypes)
            .ToListAsync(cancellationToken);

        return new AuditFacetsDto
        {
            ActorTypes = actorTypes,
            Actions = actions,
            EntityTypes = entityTypes
        };
    }
}
