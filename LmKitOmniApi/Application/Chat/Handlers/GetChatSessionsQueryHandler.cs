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
            var sessions = await _dbContext.ChatSessions
                .Where(x => x.UserId == request.UserId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new ChatSessionDto
                {
                    Id = x.Id,
                    Title = x.Title,
                    CreatedAt = x.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return sessions;
        }
    }
}
