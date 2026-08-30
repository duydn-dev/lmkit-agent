using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Application.Projects.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Projects.Handlers;

public class GetProjectSessionsQueryHandler : IRequestHandler<GetProjectSessionsQuery, List<ChatSessionDto>?>
{
    private readonly HermesDbContext _dbContext;

    public GetProjectSessionsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<ChatSessionDto>?> Handle(GetProjectSessionsQuery request, CancellationToken cancellationToken)
    {
        // A foreign project must look exactly like a missing one → null → 404.
        var projectExists = await _dbContext.Projects
            .AsNoTracking()
            .AnyAsync(project => project.Id == request.ProjectId
                && project.TenantId == request.TenantId
                && project.UserId == request.UserId,
                cancellationToken);
        if (!projectExists)
            return null;

        // Exact same DTO shape and ordering as the main sessions list
        // (GetChatSessionsQueryHandler), just restricted to the project.
        return await _dbContext.ChatSessions
            .AsNoTracking()
            .Where(session => session.ProjectId == request.ProjectId
                && session.TenantId == request.TenantId
                && session.UserId == request.UserId)
            .OrderByDescending(session => session.CreatedAt)
            .Select(ChatSessionProjections.ToDto)
            .ToListAsync(cancellationToken);
    }
}
