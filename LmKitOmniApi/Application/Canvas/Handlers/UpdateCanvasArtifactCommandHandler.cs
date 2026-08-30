using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class UpdateCanvasArtifactCommandHandler : IRequestHandler<UpdateCanvasArtifactCommand, CanvasArtifactUpdatedDto?>
{
    private readonly HermesDbContext _dbContext;

    public UpdateCanvasArtifactCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CanvasArtifactUpdatedDto?> Handle(UpdateCanvasArtifactCommand request, CancellationToken cancellationToken)
    {
        // Ownership gate + version source in one read: the latest row of the
        // caller's root. A miss (missing or foreign root) is null → 404.
        var latest = await _dbContext.CanvasArtifacts
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId
                && c.UserId == request.UserId
                && c.RootId == request.RootId)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest == null) return null;

        // Known-benign race, accepted for v1: two concurrent saves of the same
        // root can both read max version N and each insert N + 1. There is no
        // unique index on (RootId, Version), so both inserts succeed and no
        // conflict is raisable to retry on. Read paths degrade gracefully:
        // "latest" endpoints ORDER BY Version DESC and take one row, the
        // history simply lists both saves, and DELETE removes them all. A
        // unique index plus retry-on-conflict can tighten this later without
        // changing the API contract.
        var next = new CanvasArtifact
        {
            Id = Guid.NewGuid(),
            RootId = latest.RootId,
            TenantId = latest.TenantId,
            UserId = latest.UserId,
            ChatSessionId = latest.ChatSessionId,
            Title = request.Title ?? latest.Title,
            Kind = latest.Kind,
            Language = latest.Language,
            Content = request.Content,
            Version = latest.Version + 1,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.CanvasArtifacts.Add(next);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CanvasArtifactUpdatedDto { Id = next.Id, Version = next.Version };
    }
}
