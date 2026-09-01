using MediatR;
using System;

namespace LmKitOmniApi.Application.Chat.Commands
{
    public class RenameChatSessionCommand : IRequest<bool>
    {
        public Guid SessionId { get; set; }
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; } = string.Empty;
    }
}
