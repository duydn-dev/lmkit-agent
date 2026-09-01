using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Share.Commands;
using LmKitOmniApi.Infrastructure.Data;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Share.Handlers
{
    public class RevokeShareLinksCommandHandler : IRequestHandler<RevokeShareLinksCommand, bool>
    {
        private readonly HermesDbContext _dbContext;

        public RevokeShareLinksCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<bool> Handle(RevokeShareLinksCommand request, CancellationToken cancellationToken)
        {
            var ownsSession = await _dbContext.ChatSessions
                .AnyAsync(
                    s => s.Id == request.SessionId
                        && s.TenantId == request.TenantId
                        && s.UserId == request.UserId,
                    cancellationToken);
            if (!ownsSession) return false;

            var activeLinks = await _dbContext.ChatShareLinks
                .Where(l => l.ChatSessionId == request.SessionId && l.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            if (activeLinks.Count > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var link in activeLinks)
                    link.RevokedAtUtc = now;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // Owned session with nothing to revoke is still success: the end state
            // ("no active links") is what the caller asked for.
            return true;
        }
    }
}
