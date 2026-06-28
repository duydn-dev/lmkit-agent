using MediatR;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Application.Chat.Commands;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    public class CreateChatSessionCommandHandler : IRequestHandler<CreateChatSessionCommand, ChatSessionDto>
    {
        private readonly HermesDbContext _dbContext;

        public CreateChatSessionCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<ChatSessionDto> Handle(CreateChatSessionCommand request, CancellationToken cancellationToken)
        {
            var user = await _dbContext.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null) throw new Exception("User not found");

            var session = new ChatSession
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                TenantId = user.TenantId,
                Title = request.Title,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ChatSessions.Add(session);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new ChatSessionDto
            {
                Id = session.Id,
                Title = session.Title,
                CreatedAt = session.CreatedAt
            };
        }
    }
}
