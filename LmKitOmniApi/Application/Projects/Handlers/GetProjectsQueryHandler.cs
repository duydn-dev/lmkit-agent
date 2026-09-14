using LmKitOmniApi.Application.Projects.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.Projects.Handlers;

public class GetProjectsQueryHandler : IRequestHandler<GetProjectsQuery, Common.PagedResult<ProjectDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetProjectsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Common.PagedResult<ProjectDto>> Handle(GetProjectsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Projects
            .AsNoTracking()
            .Where(project => project.TenantId == request.TenantId && project.UserId == request.UserId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(project => EF.Functions.Like(project.Name, pattern)
                || (project.Description != null && EF.Functions.Like(project.Description, pattern)));
        }

        // SessionCount is computed inside the projection (one correlated
        // subquery, no N+1 round-trips) and counts the caller's own sessions in
        // the project — the only sessions that can reference it, since binding
        // is validated against ownership.
        return await query
            .OrderByDescending(project => project.CreatedAtUtc)
            .Select(project => new ProjectDto
            {
                Id = project.Id,
                Name = project.Name,
                Description = project.Description,
                Icon = project.Icon,
                Instructions = project.Instructions,
                SessionCount = _dbContext.ChatSessions.Count(session =>
                    session.ProjectId == project.Id
                    && session.TenantId == request.TenantId
                    && session.UserId == request.UserId),
                CreatedAt = project.CreatedAtUtc,
                UpdatedAt = project.UpdatedAtUtc
            })
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
