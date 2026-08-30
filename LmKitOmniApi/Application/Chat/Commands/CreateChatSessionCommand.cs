using MediatR;
using System;
using LmKitOmniApi.Application.Chat.Queries;

namespace LmKitOmniApi.Application.Chat.Commands
{
    public class CreateChatSessionCommand : IRequest<CreateChatSessionResult>
    {
        /// <summary>
        /// Default title stamped on a new chat session. Single source of truth: business logic that
        /// keys off the untouched default — such as first-message title auto-generation in
        /// StreamChatCommandHandler — references this constant instead of duplicating the literal.
        /// </summary>
        public const string DefaultChatTitle = "Đoạn chat mới";

        public Guid UserId { get; set; }
        public string Title { get; set; } = DefaultChatTitle;

        /// <summary>
        /// Optional custom agent (Gems-style persona) to bind to the session. Must
        /// exist in the caller's tenant and be owned by or shared with the caller;
        /// otherwise the handler answers with a validation error (→ 400).
        /// </summary>
        public Guid? CustomAgentId { get; set; }
    }

    /// <summary>
    /// Outcome of session creation. <see cref="ErrorMessage"/> non-null means the
    /// requested custom agent was invalid/unavailable and the controller returns
    /// 400 with that Vietnamese message; otherwise <see cref="Session"/> is set.
    /// </summary>
    public sealed class CreateChatSessionResult
    {
        public ChatSessionDto? Session { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
