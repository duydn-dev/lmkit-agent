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

        /// <summary>
        /// Optional project to create the session inside. Must be one of the
        /// caller's own projects (tenant+user scoped); otherwise the handler
        /// answers with a validation error (→ 400).
        /// </summary>
        public Guid? ProjectId { get; set; }

        /// <summary>
        /// Create the session as a temporary ("Chat tạm thời") conversation
        /// (ChatGPT/Gemini style): its messages are never persisted and it is
        /// excluded from the chat history list/search. Defaults to false.
        /// </summary>
        public bool Ephemeral { get; set; }
    }

    /// <summary>
    /// OPTIONAL JSON body of <c>POST /api/chat/sessions</c>. Wire shape:
    /// <c>{ "customAgentId": guid?, "projectId": guid? }</c>. Omitting the body
    /// (or any field) creates a plain session exactly as before; ChatController
    /// reads it manually so empty-body requests stay valid (supersedes the
    /// pre-project <c>Models.CreateChatSessionRequest</c>, which only carried
    /// <c>customAgentId</c>).
    /// </summary>
    public sealed class CreateChatSessionRequestBody
    {
        public Guid? CustomAgentId { get; set; }
        public Guid? ProjectId { get; set; }

        /// <summary>Start the session as a temporary ("Chat tạm thời") conversation.</summary>
        public bool Ephemeral { get; set; }
    }

    /// <summary>
    /// Outcome of session creation. <see cref="ErrorMessage"/> non-null means the
    /// requested custom agent or project was invalid/unavailable and the controller
    /// returns 400 with that Vietnamese message; otherwise <see cref="Session"/> is set.
    /// </summary>
    public sealed class CreateChatSessionResult
    {
        public ChatSessionDto? Session { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
