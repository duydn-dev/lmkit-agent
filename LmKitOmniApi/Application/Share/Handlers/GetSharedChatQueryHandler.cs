using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Share.Queries;
using LmKitOmniApi.Infrastructure.Data;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Share.Handlers
{
    public class GetSharedChatQueryHandler : IRequestHandler<GetSharedChatQuery, SharedChatResult>
    {
        private readonly HermesDbContext _dbContext;

        public GetSharedChatQueryHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<SharedChatResult> Handle(GetSharedChatQuery request, CancellationToken cancellationToken)
        {
            // Hash whatever was presented and probe the unique index once. The raw token
            // is never logged and never touches a query string here.
            var token = request.Token;
            if (string.IsNullOrWhiteSpace(token) || token.Length > ShareLinkToken.MaxPresentedLength)
                return SharedChatResult.NotFound;

            var tokenHash = ShareLinkToken.Hash(token);

            // Step 1 — the link's own state. Kept separate from the transcript projection
            // so a revoked or expired link never causes the messages to be read at all:
            // the refusal path touches one row on a unique index and stops there.
            var link = await _dbContext.ChatShareLinks
                .AsNoTracking()
                .Where(l => l.TokenHash == tokenHash)
                .Select(l => new { l.Id, l.RevokedAtUtc, l.ExpiresAtUtc })
                .FirstOrDefaultAsync(cancellationToken);

            // Unknown token: one opaque answer, same as a deleted session below.
            if (link is null) return SharedChatResult.NotFound;

            // Revocation is checked BEFORE expiry, so a link that is both reports
            // "revoked". Not an arbitrary tie-break: revocation is a deliberate act by
            // the owner and expiry is the default that happens to everything, so when
            // both are true the owner's decision is the honest explanation. It is also
            // the stabler of the two — a later change to the configured TTL cannot move
            // an expiry timestamp already written to the row, but it CAN change which of
            // the two a naive "earliest wins" rule would pick.
            if (link.RevokedAtUtc is { } revokedAtUtc)
                return SharedChatResult.Revoked(revokedAtUtc);

            // The deadline. Compared in the application rather than in the WHERE clause
            // precisely so the caller can be told which gate closed; a `WHERE ExpiresAtUtc
            // > now` predicate would fold expiry back into the same silent nothing that
            // this change exists to split apart.
            if (link.ExpiresAtUtc <= DateTime.UtcNow)
                return SharedChatResult.Expired(link.ExpiresAtUtc);

            // Step 2 — the transcript, reached only by a link that passed both gates.
            var chat = await _dbContext.ChatShareLinks
                .AsNoTracking()
                .Where(l => l.Id == link.Id)
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

            // Live link whose session was deleted: falls into the same opaque bucket as
            // an unknown token, because there is nothing left to describe.
            return chat is null ? SharedChatResult.NotFound : SharedChatResult.Ok(chat);
        }
    }
}
