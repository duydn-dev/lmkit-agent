using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Infrastructure.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    public class GetChatSessionsQueryHandler : IRequestHandler<GetChatSessionsQuery, List<ChatSessionDto>>
    {
        private readonly HermesDbContext _dbContext;

        public GetChatSessionsQueryHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<ChatSessionDto>> Handle(GetChatSessionsQuery request, CancellationToken cancellationToken)
        {
            var scopedSessions = _dbContext.ChatSessions
                .Where(x => x.UserId == request.UserId);

            // Optional exact-match project filter; absent = unchanged full list.
            if (request.ProjectId is Guid projectId)
            {
                scopedSessions = scopedSessions.Where(x => x.ProjectId == projectId);
            }

            var sessions = await scopedSessions
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new ChatSessionDto
                {
                    Id = x.Id,
                    Title = x.Title,
                    CreatedAt = x.CreatedAt,
                    CustomAgentId = x.CustomAgentId,
                    AgentName = x.CustomAgent != null ? x.CustomAgent.Name : null,
                    AgentIcon = x.CustomAgent != null ? x.CustomAgent.Icon : null,
                    ProjectId = x.ProjectId
                })
                .ToListAsync(cancellationToken);

            return sessions;
        }
    }
}
