using MediatR;
using System;
using System.Collections.Generic;

namespace LmKitOmniApi.Application.Chat.Queries
{
    public class GetChatSessionsQuery : IRequest<List<ChatSessionDto>>
    {
        public Guid UserId { get; set; }

        /// <summary>
        /// Optional exact-match project filter (<c>?projectId=</c>). Null keeps
        /// the pre-existing default behavior: every session of the caller.
        /// </summary>
        public Guid? ProjectId { get; set; }
    }

    public class ChatSessionDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        // Custom-agent binding (Gems-style). Additive nullable fields: sessions
        // without an agent keep the exact pre-existing shape plus nulls.
        public Guid? CustomAgentId { get; set; }
        public string? AgentName { get; set; }
        public string? AgentIcon { get; set; }

        /// <summary>Project the session belongs to (additive nullable field).</summary>
        public Guid? ProjectId { get; set; }
    }
}
