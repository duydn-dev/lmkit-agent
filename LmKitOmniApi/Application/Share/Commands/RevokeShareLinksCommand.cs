using MediatR;
using System;

namespace LmKitOmniApi.Application.Share.Commands
{
    /// <summary>
    /// Revokes every active share link for a caller-owned chat session. Returns false
    /// when the session does not exist for this tenant/user (controller maps to 404);
    /// revoking a session that simply has no active links still succeeds.
    /// </summary>
    public class RevokeShareLinksCommand : IRequest<bool>
    {
        public Guid SessionId { get; set; }
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }
    }
}
