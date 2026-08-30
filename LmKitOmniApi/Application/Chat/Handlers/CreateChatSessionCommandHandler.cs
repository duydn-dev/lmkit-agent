using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Application.Chat.Commands;

namespace LmKitOmniApi.Application.Chat.Handlers
{
    public class CreateChatSessionCommandHandler : IRequestHandler<CreateChatSessionCommand, CreateChatSessionResult>
    {
        private readonly HermesDbContext _dbContext;

        public CreateChatSessionCommandHandler(HermesDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<CreateChatSessionResult> Handle(CreateChatSessionCommand request, CancellationToken cancellationToken)
        {
            var user = await _dbContext.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null) throw new Exception("User not found");

            // Optional custom-agent binding: the agent must live in the caller's
            // tenant and be either owned by the caller or shared with the tenant.
            CustomAgent? agent = null;
            if (request.CustomAgentId is Guid customAgentId)
            {
                agent = await _dbContext.CustomAgents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == customAgentId
                        && candidate.TenantId == user.TenantId
                        && (candidate.OwnerUserId == request.UserId || candidate.IsSharedWithTenant),
                        cancellationToken);
                if (agent is null)
                {
                    return new CreateChatSessionResult
                    {
                        ErrorMessage = "Agent không tồn tại hoặc bạn không có quyền dùng"
                    };
                }
            }

            var session = new ChatSession
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                TenantId = user.TenantId,
                Title = string.IsNullOrWhiteSpace(request.Title) ? CreateChatSessionCommand.DefaultChatTitle : request.Title,
                CreatedAt = DateTime.UtcNow,
                CustomAgentId = agent?.Id
            };

            _dbContext.ChatSessions.Add(session);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new CreateChatSessionResult
            {
                Session = new ChatSessionDto
                {
                    Id = session.Id,
                    Title = session.Title,
                    CreatedAt = session.CreatedAt,
                    CustomAgentId = agent?.Id,
                    AgentName = agent?.Name,
                    AgentIcon = agent?.Icon
                }
            };
        }
    }
}
