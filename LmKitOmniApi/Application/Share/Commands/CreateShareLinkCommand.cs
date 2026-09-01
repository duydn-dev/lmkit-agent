using MediatR;
using System;

namespace LmKitOmniApi.Application.Share.Commands
{
    /// <summary>
    /// Rotates the share link for a caller-owned chat session: revokes any still-active
    /// links, then mints a fresh token. Returns the raw token (which is never persisted),
    /// or null when the session does not exist for this tenant/user — the controller maps
    /// that to 404 so foreign sessions are indistinguishable from missing ones.
    /// </summary>
    public class CreateShareLinkCommand : IRequest<string?>
    {
        public Guid SessionId { get; set; }
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }
    }
}
