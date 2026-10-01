using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Chat.Queries;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    public class GetChatMessagesQueryHandler : IRequestHandler<GetChatMessagesQuery, List<ChatMessageDto>?>
    {
        private readonly HermesDbContext _dbContext;

        public GetChatMessagesQueryHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<ChatMessageDto>?> Handle(GetChatMessagesQuery request, CancellationToken cancellationToken)
        {
            var hasAccess = await _dbContext.ChatSessions
                .AnyAsync(s => s.Id == request.SessionId && s.UserId == request.UserId, cancellationToken);
                
            if (!hasAccess) return null;

            var rows = await _dbContext.ChatMessages
                .Where(m => m.ChatSessionId == request.SessionId)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt })
                .ToListAsync(cancellationToken);

            return rows.Select(m => new ChatMessageDto
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                CreatedAt = m.CreatedAt,
                Clarification = ExtractClarification(m.Content)
            }).ToList();
        }

        private static LmKitOmniApi.Infrastructure.AI.ClarificationRequest? ExtractClarification(string content)
        {
            var match = System.Text.RegularExpressions.Regex.Match(content ?? string.Empty,
                @"\[CLARIFICATION:(\{[^\r\n]*\})\]");
            if (!match.Success) return null;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<LmKitOmniApi.Infrastructure.AI.ClarificationRequest>(
                    match.Groups[1].Value,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}
