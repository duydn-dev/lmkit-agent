using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Share.Queries;
using LmKitOmniApi.Infrastructure.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Share.Handlers
{
    public class GetSharedChatQueryHandler : IRequestHandler<GetSharedChatQuery, SharedChatDto?>
    {
        private readonly HermesDbContext _dbContext;

        public GetSharedChatQueryHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<SharedChatDto?> Handle(GetSharedChatQuery request, CancellationToken cancellationToken)
        {
            // Constant-shape lookup: hash whatever was presented and probe the unique
            // index once. Unknown, revoked, and session-less tokens all fall through to
            // the same null → identical 404, so responses reveal nothing about token
            // state. The raw token is never logged and never touches a query string here.
            var token = request.Token;
            if (string.IsNullOrWhiteSpace(token) || token.Length > ShareLinkToken.MaxPresentedLength)
                return null;

            var tokenHash = ShareLinkToken.Hash(token);

            return await _dbContext.ChatShareLinks
                .AsNoTracking()
                .Where(l => l.TokenHash == tokenHash && l.RevokedAtUtc == null)
                .Select(l => new SharedChatDto
                {
                    // Inner join through the required navigation: a vanished session
                    // yields no row, which is exactly the 404 the contract requires.
                    Title = l.ChatSession!.Title,
                    CreatedAt = l.ChatSession!.CreatedAt,
                    Messages = l.ChatSession!.Messages
                        .Where(m => m.Role == "user" || m.Role == "assistant")
                        .OrderBy(m => m.CreatedAt)
                        .Select(m => new SharedChatMessageDto
                        {
                            Role = m.Role,
                            Content = m.Content,
                            CreatedAt = m.CreatedAt
                        })
                        .ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
