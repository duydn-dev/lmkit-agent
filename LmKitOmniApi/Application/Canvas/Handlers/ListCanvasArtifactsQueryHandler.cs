using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class ListCanvasArtifactsQueryHandler : IRequestHandler<ListCanvasArtifactsQuery, List<CanvasArtifactListItemDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListCanvasArtifactsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<CanvasArtifactListItemDto>> Handle(ListCanvasArtifactsQuery request, CancellationToken cancellationToken)
    {
        var owned = _dbContext.CanvasArtifacts
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId && c.UserId == request.UserId);

        // ChatSessionId is stamped onto every version row of a root, so
        // filtering before the latest-per-root step never splits a family.
        if (request.SessionId is { } sessionId)
            owned = owned.Where(c => c.ChatSessionId == sessionId);

        // Latest-per-root as an anti-join: a row is the latest exactly when no
        // higher-version row shares its RootId. This NOT EXISTS shape is used
        // deliberately — GroupBy(...).Select(g => g.OrderByDescending(...).First())
        // does not translate on every provider, while a correlated Any(...)
        // translates on both Npgsql and SQLite. The inner predicate re-scopes to
        // the tenant purely for index locality; RootId is a GUID unique to one
        // artifact family, so the scoping does not change the result.
        return await owned
            .Where(c => !_dbContext.CanvasArtifacts.Any(o =>
                o.TenantId == request.TenantId
                && o.RootId == c.RootId
                && o.Version > c.Version))
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id) // deterministic order for same-instant saves
            .Take(100)
            .Select(c => new CanvasArtifactListItemDto
            {
                Id = c.Id,
                RootId = c.RootId,
                Title = c.Title,
                Kind = c.Kind,
                Language = c.Language,
                Version = c.Version,
                ChatSessionId = c.ChatSessionId,
                UpdatedAt = c.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
