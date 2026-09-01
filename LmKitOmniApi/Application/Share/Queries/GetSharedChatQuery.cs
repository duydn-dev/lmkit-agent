using MediatR;
using System;
using System.Collections.Generic;

namespace LmKitOmniApi.Application.Share.Queries
{
    /// <summary>
    /// Resolves a presented share token to its public read-only transcript. Returns
    /// null — and therefore an identical 404 — for unknown, revoked, and orphaned
    /// tokens alike, so the endpoint is not an oracle for token state.
    /// </summary>
    public class GetSharedChatQuery : IRequest<SharedChatDto?>
    {
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>Public share payload: intentionally excludes ids, tenant, and user data.</summary>
    public class SharedChatDto
    {
        public string Title { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public List<SharedChatMessageDto> Messages { get; set; } = new();
    }

    public class SharedChatMessageDto
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
