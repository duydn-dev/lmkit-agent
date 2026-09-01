using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Commands;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class DeleteCanvasArtifactCommandHandler : IRequestHandler<DeleteCanvasArtifactCommand, bool>
{
    private readonly HermesDbContext _dbContext;

    public DeleteCanvasArtifactCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> Handle(DeleteCanvasArtifactCommand request, CancellationToken cancellationToken)
    {
        // Tracked delete (not ExecuteDelete) on purpose: SaveChanges is where
        // AuditSaveChangesInterceptor records each removed version row.
        var versions = await _dbContext.CanvasArtifacts
            .Where(c => c.TenantId == request.TenantId
                && c.UserId == request.UserId
                && c.RootId == request.RootId)
            .ToListAsync(cancellationToken);
        if (versions.Count == 0) return false;

        _dbContext.CanvasArtifacts.RemoveRange(versions);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
