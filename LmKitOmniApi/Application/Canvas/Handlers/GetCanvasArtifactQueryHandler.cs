using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class GetCanvasArtifactQueryHandler : IRequestHandler<GetCanvasArtifactQuery, CanvasArtifactDetailDto?>
{
    private readonly HermesDbContext _dbContext;

    public GetCanvasArtifactQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CanvasArtifactDetailDto?> Handle(GetCanvasArtifactQuery request, CancellationToken cancellationToken)
    {
        // Ownership gate first: foreign roots fall out of the filter and
        // surface as the same null → 404 as truly missing ones.
        var versions = _dbContext.CanvasArtifacts
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId
                && c.UserId == request.UserId
                && c.RootId == request.RootId);

        // No version → latest (ORDER BY Version DESC LIMIT 1);
        // explicit version → that exact row or null.
        IQueryable<CanvasArtifact> selected = request.Version is { } exactVersion
            ? versions.Where(c => c.Version == exactVersion)
            : versions.OrderByDescending(c => c.Version);

        return await selected
            .Select(c => new CanvasArtifactDetailDto
            {
                Id = c.Id,
                RootId = c.RootId,
                Title = c.Title,
                Kind = c.Kind,
                Language = c.Language,
                Version = c.Version,
                Content = c.Content,
                ChatSessionId = c.ChatSessionId,
                CreatedAt = c.CreatedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
