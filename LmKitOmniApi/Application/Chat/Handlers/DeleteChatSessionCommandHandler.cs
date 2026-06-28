using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Chat.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    public class DeleteChatSessionCommandHandler : IRequestHandler<DeleteChatSessionCommand, bool>
    {
        private readonly HermesDbContext _dbContext;

        public DeleteChatSessionCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<bool> Handle(DeleteChatSessionCommand request, CancellationToken cancellationToken)
        {
            var session = await _dbContext.ChatSessions
                .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.UserId == request.UserId, cancellationToken);

            if (session == null) return false;

            _dbContext.ChatSessions.Remove(session);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
