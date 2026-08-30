using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class ListCanvasVersionsQueryHandler : IRequestHandler<ListCanvasVersionsQuery, List<CanvasVersionDto>?>
{
    private readonly HermesDbContext _dbContext;

    public ListCanvasVersionsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<CanvasVersionDto>?> Handle(ListCanvasVersionsQuery request, CancellationToken cancellationToken)
    {
        var versions = await _dbContext.CanvasArtifacts
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId
                && c.UserId == request.UserId
                && c.RootId == request.RootId)
            .OrderByDescending(c => c.Version)
            .Select(c => new CanvasVersionDto
            {
                Id = c.Id,
                Version = c.Version,
                CreatedAt = c.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        // Every existing root has at least one version, so an empty history can
        // only mean "missing or foreign" — map it to null so the controller's
        // 404 keeps both cases indistinguishable.
        return versions.Count == 0 ? null : versions;
    }
}
