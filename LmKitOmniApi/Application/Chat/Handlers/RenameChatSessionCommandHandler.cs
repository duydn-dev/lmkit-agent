using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Chat.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    /// <summary>
    /// Renames a chat session. Scoped by tenant AND user (mirrors
    /// <see cref="DeleteChatSessionCommandHandler"/>): a session that does not
    /// exist or belongs to another tenant/user yields <c>false</c>, which the
    /// controller maps to 404 — never 403 — so session ids are not enumerable.
    /// </summary>
    public class RenameChatSessionCommandHandler : IRequestHandler<RenameChatSessionCommand, bool>
    {
        private readonly HermesDbContext _dbContext;

        public RenameChatSessionCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<bool> Handle(RenameChatSessionCommand request, CancellationToken cancellationToken)
        {
            var session = await _dbContext.ChatSessions
                .FirstOrDefaultAsync(s => s.Id == request.SessionId
                    && s.TenantId == request.TenantId
                    && s.UserId == request.UserId, cancellationToken);

            if (session == null) return false;

            session.Title = request.Title;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
