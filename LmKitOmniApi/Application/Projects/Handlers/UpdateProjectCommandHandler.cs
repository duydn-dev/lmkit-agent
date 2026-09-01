using LmKitOmniApi.Application.Projects.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Projects.Handlers;

public class UpdateProjectCommandHandler : IRequestHandler<UpdateProjectCommand, SaveProjectResult>
{
    private readonly HermesDbContext _dbContext;

    public UpdateProjectCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SaveProjectResult> Handle(UpdateProjectCommand request, CancellationToken cancellationToken)
    {
        var validationError = ProjectRules.Validate(request);
        if (validationError is not null)
            return SaveProjectResult.ValidationFailed(validationError);

        // Tenant+owner scoped lookup: a foreign project is indistinguishable from
        // a missing one (404, never 403).
        var project = await _dbContext.Projects.FirstOrDefaultAsync(
            candidate => candidate.Id == request.ProjectId
                && candidate.TenantId == request.TenantId
                && candidate.UserId == request.UserId,
            cancellationToken);
        if (project is null)
            return SaveProjectResult.NotFound();

        // Validate above guarantees a non-empty name.
        project.Name = request.Name!.Trim();
        project.Description = ProjectRules.NormalizeOptional(request.Description);
        project.Icon = ProjectRules.NormalizeOptional(request.Icon);
        project.Instructions = ProjectRules.NormalizeOptional(request.Instructions);
        project.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return SaveProjectResult.Success();
    }
}
