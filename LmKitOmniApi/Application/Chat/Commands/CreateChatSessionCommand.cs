using MediatR;
using System;
using LmKitOmniApi.Application.Chat.Queries;

namespace LmKitOmniApi.Application.Chat.Commands
{
    public class CreateChatSessionCommand : IRequest<ChatSessionDto>
    {
        /// <summary>
        /// Default title stamped on a new chat session. Single source of truth: business logic that
        /// keys off the untouched default — such as first-message title auto-generation in
        /// StreamChatCommandHandler — references this constant instead of duplicating the literal.
        /// </summary>
        public const string DefaultChatTitle = "Đoạn chat mới";

        public Guid UserId { get; set; }
        public string Title { get; set; } = DefaultChatTitle;
    }
}
