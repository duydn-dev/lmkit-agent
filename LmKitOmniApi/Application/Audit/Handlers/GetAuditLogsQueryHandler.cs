using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Audit.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Audit.Handlers;

public sealed class GetAuditLogsQueryHandler : IRequestHandler<GetAuditLogsQuery, AuditLogPageDto>
{
    private const int MaxPageSize = 100;

    private readonly HermesDbContext _dbContext;

    public GetAuditLogsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AuditLogPageDto> Handle(GetAuditLogsQuery request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var query = _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => log.TenantId == request.TenantId);

        if (!string.IsNullOrWhiteSpace(request.ActorType))
        {
            var actorType = request.ActorType.Trim();
            query = query.Where(log => log.ActorType == actorType);
        }

        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            var action = request.Action.Trim();
            query = query.Where(log => log.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            var entityType = request.EntityType.Trim();
            query = query.Where(log => log.EntityType.ToLower().Contains(entityType.ToLower()));
        }

        if (request.FromUtc is { } fromUtc)
            query = query.Where(log => log.CreatedAtUtc >= fromUtc);

        if (request.ToUtc is { } toUtc)
            query = query.Where(log => log.CreatedAtUtc <= toUtc);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(log => log.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new AuditLogDto
            {
                Id = log.Id,
                ActorUserId = log.ActorUserId,
                ActorType = log.ActorType,
                Action = log.Action,
                EntityType = log.EntityType,
                EntityId = log.EntityId,
                CorrelationId = log.CorrelationId,
                DetailsJson = log.DetailsJson,
                CreatedAtUtc = log.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new AuditLogPageDto
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
