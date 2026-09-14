using LmKitOmniApi.Application.Common;
using LmKitOmniApi.Application.DatabaseConnections.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.DatabaseConnections.Handlers;

public sealed class GetDatabaseConnectionsQueryHandler : IRequestHandler<GetDatabaseConnectionsQuery, PagedResult<DatabaseConnectionDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetDatabaseConnectionsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<DatabaseConnectionDto>> Handle(GetDatabaseConnectionsQuery request, CancellationToken cancellationToken)
    {
        // Hàng của tenant hiện tại + hàng toàn hệ thống (TenantId null).
        var query = _dbContext.DatabaseConnections
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId || c.TenantId == null);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(c => EF.Functions.Like(c.Name, pattern) || EF.Functions.Like(c.Provider, pattern));
        }

        // Projection intentionally omits ConnectionStringProtected — the secret is
        // never returned to any client.
        return await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new DatabaseConnectionDto
            {
                Id = c.Id,
                TenantId = c.TenantId,
                TenantName = c.Tenant != null ? c.Tenant.Name : null,
                Name = c.Name,
                Provider = c.Provider,
                IsActive = c.IsActive,
                AllowWrites = c.AllowWrites,
                IsIndexed = c.IsIndexed,
                IndexStatus = c.IndexStatus,
                LastIndexError = c.LastIndexError,
                LastIndexedAtUtc = c.LastIndexedAtUtc,
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
