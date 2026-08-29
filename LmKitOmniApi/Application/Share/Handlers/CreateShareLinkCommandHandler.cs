using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Share.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Share.Handlers
{
    public class CreateShareLinkCommandHandler : IRequestHandler<CreateShareLinkCommand, string?>
    {
        private readonly HermesDbContext _dbContext;

        public CreateShareLinkCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<string?> Handle(CreateShareLinkCommand request, CancellationToken cancellationToken)
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
            var activeLinks = await _dbContext.ChatShareLinks
                .Where(l => l.ChatSessionId == session.Id && l.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var link in activeLinks)
                link.RevokedAtUtc = now;

            var rawToken = ShareLinkToken.Generate();
            _dbContext.ChatShareLinks.Add(new ChatShareLink
            {
                ChatSessionId = session.Id,
                TenantId = session.TenantId,
                TokenHash = ShareLinkToken.Hash(rawToken),
                CreatedAtUtc = now
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Only the hash was stored; the raw token's sole existence is this response.
            return rawToken;
        }
    }
}
