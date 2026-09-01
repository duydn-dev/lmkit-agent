using LmKitOmniApi.Application.Projects.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Projects.Handlers;

public class DeleteProjectCommandHandler : IRequestHandler<DeleteProjectCommand, bool>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<DeleteProjectCommandHandler> _logger;

    public DeleteProjectCommandHandler(HermesDbContext dbContext, ILogger<DeleteProjectCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<bool> Handle(DeleteProjectCommand request, CancellationToken cancellationToken)
    {
        var project = await _dbContext.Projects.FirstOrDefaultAsync(
            candidate => candidate.Id == request.ProjectId
                && candidate.TenantId == request.TenantId
                && candidate.UserId == request.UserId,
            cancellationToken);
        if (project is null)
            return false;

        // The ChatSession→Project FK is SetNull: the project's sessions survive
        // and simply leave the project (ProjectId becomes null).
        _dbContext.Projects.Remove(project);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Project {ProjectId} deleted by user {UserId} (tenant {TenantId})",
            request.ProjectId, request.UserId, request.TenantId);

        return true;
    }
}
