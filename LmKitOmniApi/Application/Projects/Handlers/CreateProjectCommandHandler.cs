using LmKitOmniApi.Application.Projects.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Projects.Handlers;

public class CreateProjectCommandHandler : IRequestHandler<CreateProjectCommand, SaveProjectResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<CreateProjectCommandHandler> _logger;

    public CreateProjectCommandHandler(HermesDbContext dbContext, ILogger<CreateProjectCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<SaveProjectResult> Handle(CreateProjectCommand request, CancellationToken cancellationToken)
    {
        var validationError = ProjectRules.Validate(request);
        if (validationError is not null)
            return SaveProjectResult.ValidationFailed(validationError);

        var ownedProjectCount = await _dbContext.Projects.CountAsync(
            project => project.TenantId == request.TenantId && project.UserId == request.UserId,
            cancellationToken);
        if (ownedProjectCount >= ProjectRules.MaxProjectsPerUser)
            return SaveProjectResult.ValidationFailed(
                $"Bạn đã đạt giới hạn tối đa {ProjectRules.MaxProjectsPerUser} dự án.");

        var now = DateTime.UtcNow;
        var project = new Project
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            // Validate above guarantees a non-empty name.
            Name = request.Name!.Trim(),
            Description = ProjectRules.NormalizeOptional(request.Description),
            Icon = ProjectRules.NormalizeOptional(request.Icon),
            Instructions = ProjectRules.NormalizeOptional(request.Instructions),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Project {ProjectId} created by user {UserId} (tenant {TenantId})",
            project.Id, request.UserId, request.TenantId);

        // A just-created project cannot contain sessions yet.
        return SaveProjectResult.Success(ProjectRules.ToDto(project, sessionCount: 0));
    }
}
