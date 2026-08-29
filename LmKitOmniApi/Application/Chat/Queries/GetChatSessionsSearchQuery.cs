using MediatR;
using System;
using System.Collections.Generic;

namespace LmKitOmniApi.Application.Chat.Queries
{
    public class GetChatSessionsSearchQuery : IRequest<List<ChatSessionDto>>
    {
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }

        /// <summary>Search text. Empty/whitespace returns the full session list.</summary>
        public string Q { get; set; } = string.Empty;
    }
}
