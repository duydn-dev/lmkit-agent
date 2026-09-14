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
                .Where(x => x.UserId == request.UserId && !x.IsAgentRun && !x.IsEphemeral);

            // Optional exact-match project filter; absent = unchanged full list.
            if (request.ProjectId is Guid projectId)
            {
                scopedSessions = scopedSessions.Where(x => x.ProjectId == projectId);
            }

            // Keyset pagination (?before=<iso>&limit=N): only when Limit is set —
            // absent keeps the full-list behavior for existing callers.
            if (request.Limit > 0)
            {
                if (request.Before is DateTime before)
                {
                    scopedSessions = scopedSessions.Where(x => x.CreatedAt < before);
                }

                return await scopedSessions
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(request.Limit)
                    .Select(ChatSessionProjections.ToDto)
                    .ToListAsync(cancellationToken);
            }

            var sessions = await scopedSessions
                .OrderByDescending(x => x.CreatedAt)
                .Select(ChatSessionProjections.ToDto)
                .ToListAsync(cancellationToken);

            return sessions;
        }
    }
}
