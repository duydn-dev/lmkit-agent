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
    /// <summary>
    /// Searches the caller's chat sessions by title or message content.
    /// Empty/whitespace <c>Q</c> degrades to the normal full list (same shape and
    /// ordering as <see cref="GetChatSessionsQueryHandler"/>); a non-empty term is
    /// matched case-insensitively via <c>ToLower().Contains(...)</c> so the filter
    /// translates to SQL on both Npgsql and SQLite, and results are capped at 50.
    /// </summary>
    public class GetChatSessionsSearchQueryHandler : IRequestHandler<GetChatSessionsSearchQuery, List<ChatSessionDto>>
    {
        private const int MaxResults = 50;

        private readonly HermesDbContext _dbContext;

        public GetChatSessionsSearchQueryHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<ChatSessionDto>> Handle(GetChatSessionsSearchQuery request, CancellationToken cancellationToken)
        {
            var scopedSessions = _dbContext.ChatSessions
                .AsNoTracking()
                .Where(s => s.TenantId == request.TenantId && s.UserId == request.UserId);

            if (string.IsNullOrWhiteSpace(request.Q))
            {
                return await scopedSessions
                    .OrderByDescending(s => s.CreatedAt)
                    .Select(ChatSessionProjections.ToDto)
                    .ToListAsync(cancellationToken);
            }

            var term = request.Q.Trim().ToLower();

            return await scopedSessions
                .Where(s => s.Title.ToLower().Contains(term)
                    || s.Messages.Any(m => m.Content.ToLower().Contains(term)))
                .OrderByDescending(s => s.CreatedAt)
                .Take(MaxResults)
                .Select(ChatSessionProjections.ToDto)
                .ToListAsync(cancellationToken);
        }
    }
}
