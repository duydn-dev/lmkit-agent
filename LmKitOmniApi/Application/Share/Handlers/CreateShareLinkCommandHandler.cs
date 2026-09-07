using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LmKitOmniApi.Application.Share.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Share.Handlers
{
    public class CreateShareLinkCommandHandler : IRequestHandler<CreateShareLinkCommand, CreatedShareLink?>
    {
        private readonly HermesDbContext _dbContext;
        private readonly ShareLinkOptions _options;

        public CreateShareLinkCommandHandler(HermesDbContext dbContext, IOptions<ShareLinkOptions> options)
        {
            _dbContext = dbContext;
            // IOptions<T> resolves from the default options infrastructure even when
            // AddShareLinks was never called, so an un-updated Program.cs yields the
            // entity's own DefaultTimeToLiveDays rather than an unbounded link.
            _options = options.Value;
        }

        public async Task<CreatedShareLink?> Handle(CreateShareLinkCommand request, CancellationToken cancellationToken)
        {
            // Ownership gate: the session must belong to the caller's tenant AND user.
            // A miss returns null (→ 404) so foreign sessions look exactly like missing ones.
            var session = await _dbContext.ChatSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.Id == request.SessionId
                        && s.TenantId == request.TenantId
                        && s.UserId == request.UserId,
                    cancellationToken);
            if (session == null) return null;

            // At most one live link per session: rotating revokes everything still active
            // in the same SaveChanges, so the old URL dies the moment the new one is born.
            // Filtered on RevokedAtUtc only, NOT on the deadline: an already-expired link
            // still gets its RevokedAtUtc stamped, because leaving it unrevoked would let
            // a later TTL increase silently resurrect a URL the owner has already replaced.
            var activeLinks = await _dbContext.ChatShareLinks
                .Where(l => l.ChatSessionId == session.Id && l.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var link in activeLinks)
                link.RevokedAtUtc = now;

            // Measured from the mint, not from the session's creation: this is a fresh
            // grant of access, and the clock on it should start now.
            var expiresAtUtc = now + _options.TimeToLive;

            var rawToken = ShareLinkToken.Generate();
            _dbContext.ChatShareLinks.Add(new ChatShareLink
            {
                ChatSessionId = session.Id,
                TenantId = session.TenantId,
                TokenHash = ShareLinkToken.Hash(rawToken),
                CreatedAtUtc = now,
                ExpiresAtUtc = expiresAtUtc
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Only the hash was stored; the raw token's sole existence is this response.
            return new CreatedShareLink(rawToken, expiresAtUtc);
        }
    }
}
