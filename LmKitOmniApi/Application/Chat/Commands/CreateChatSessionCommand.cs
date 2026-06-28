using MediatR;
using System;
using LmKitOmniApi.Application.Chat.Queries;

namespace LmKitOmniApi.Application.Chat.Commands
{
    public class CreateChatSessionCommand : IRequest<ChatSessionDto>
    {
        public Guid UserId { get; set; }
        public string Title { get; set; } = "Đoạn chat mới";
    }
}
