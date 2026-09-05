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

    // A concurrent save can grab the same Version+1 slot between our read and our
    // write; the UNIQUE index on (TenantId, RootId, Version) rejects the loser, and
    // we re-read + bump. A handful of attempts absorbs realistic contention on one
    // root without letting a genuine, persistent DB fault spin forever.
    private const int MaxAttempts = 5;

    public async Task<CanvasArtifactUpdatedDto?> Handle(UpdateCanvasArtifactCommand request, CancellationToken cancellationToken)
    {
        // Ownership gate + version source in one read: the latest row of the
        // caller's root. A miss (missing or foreign root) is null → 404.
        var latest = await ReadLatestAsync(request, cancellationToken);
        if (latest == null) return null;

        // Read latest → insert Version+1, wrapped in a bounded retry. Two concurrent
        // saves of the same root can both read max version N and each try to insert
        // N+1; the UNIQUE (TenantId, RootId, Version) index lets exactly one win and
        // raises a DbUpdateException on the other. On that conflict we detach the
        // failed row, re-read the now-higher latest version and retry, so a racing
        // save yields N+2 instead of failing or duplicating a version number.
        for (var attempt = 1; ; attempt++)
        {
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

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new CanvasArtifactUpdatedDto { Id = next.Id, Version = next.Version };
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Undo the rejected insert so the context is clean, then rebase onto
                // whatever is now latest (the winning concurrent save) and retry.
                _dbContext.Entry(next).State = EntityState.Detached;

                latest = await ReadLatestAsync(request, cancellationToken);
                // The root was deleted out from under us mid-race → same 404 as a miss.
                if (latest == null) return null;
            }
        }
    }

    private Task<CanvasArtifact?> ReadLatestAsync(UpdateCanvasArtifactCommand request, CancellationToken cancellationToken) =>
        _dbContext.CanvasArtifacts
            .AsNoTracking()
            .Where(c => c.TenantId == request.TenantId
                && c.UserId == request.UserId
                && c.RootId == request.RootId)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);
}
