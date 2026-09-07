using MediatR;
using System;

namespace LmKitOmniApi.Application.Share.Commands
{
    /// <summary>
    /// Rotates the share link for a caller-owned chat session: revokes any still-active
    /// links, then mints a fresh token. Returns the raw token (which is never persisted)
    /// together with its deadline, or null when the session does not exist for this
    /// tenant/user — the controller maps that to 404 so foreign sessions are
    /// indistinguishable from missing ones.
    ///
    /// <para>Deliberately carries no lifetime input. The link's lifetime is an OPERATOR
    /// setting (<c>ShareLinks:TimeToLiveDays</c>), not a caller choice — see
    /// <c>ShareLinkOptions</c> for the argument.</para>
    /// </summary>
    public class CreateShareLinkCommand : IRequest<CreatedShareLink?>
    {
        public Guid SessionId { get; set; }
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }
    }

    /// <summary>
    /// What minting produced: the raw token — its only appearance anywhere — and the
    /// moment it stops resolving. The deadline is returned rather than left implicit
    /// because the owner is the only person who can act on it: they are the one who has
    /// to re-share before it lapses, and they cannot do that if the API never says when
    /// "before" is.
    /// </summary>
    public sealed record CreatedShareLink(string Token, DateTime ExpiresAtUtc);
}
