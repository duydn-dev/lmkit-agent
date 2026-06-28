using MediatR;
using System;

namespace LmKitOmniApi.Application.Chat.Commands
{
    public class DeleteChatSessionCommand : IRequest<bool>
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
    }
}
