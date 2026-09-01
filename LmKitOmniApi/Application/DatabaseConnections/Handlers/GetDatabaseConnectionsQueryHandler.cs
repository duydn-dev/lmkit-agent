using LmKitOmniApi.Application.DatabaseConnections.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.DatabaseConnections.Handlers;

public sealed class GetDatabaseConnectionsQueryHandler : IRequestHandler<GetDatabaseConnectionsQuery, List<DatabaseConnectionDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetDatabaseConnectionsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<List<DatabaseConnectionDto>> Handle(GetDatabaseConnectionsQuery request, CancellationToken cancellationToken)
    {
        // Projection intentionally omits ConnectionStringProtected — the secret is
        // never returned to any client.
        return await _dbContext.DatabaseConnections
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new DatabaseConnectionDto
            {
                Id = c.Id,
                Name = c.Name,
                Provider = c.Provider,
                IsActive = c.IsActive,
                IsIndexed = c.IsIndexed,
                IndexStatus = c.IndexStatus,
                LastIndexError = c.LastIndexError,
                LastIndexedAtUtc = c.LastIndexedAtUtc,
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
