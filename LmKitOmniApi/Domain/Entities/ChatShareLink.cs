using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A read-only public share link for a chat session. Only the SHA-256 hex digest of
/// the share token is ever persisted — the raw token exists solely in the creation
/// response, so a database leak cannot resurrect working share URLs. Revocation is a
/// timestamp instead of a delete so rotations leave an auditable trail.
///
/// <para>A link resolves only while it is BOTH unrevoked and unexpired. Revocation is
/// the owner pulling the link; expiry is the clock doing it for them. Both refusals are
/// surfaced as 410 Gone carrying a reason, so the two are never confused for each other
/// — see <c>GetSharedChatQueryHandler</c>.</para>
/// </summary>
[Table("chat_share_links")]
public sealed class ChatShareLink
{
    /// <summary>
    /// Fallback lifetime, in days, for a share link minted without consulting
    /// <c>ShareLinks:TimeToLiveDays</c> — it backs the <see cref="ExpiresAtUtc"/>
    /// initializer below, so a creation site that forgets the option still produces a
    /// bounded link instead of an immortal one. <c>ShareLinkOptions</c> defaults to this
    /// same value, so code and configuration cannot drift apart (the same belt-and-braces
    /// arrangement as <see cref="TaskApproval.DefaultTimeToLiveHours"/>).
    ///
    /// <para>30 days: a share link exposes a PRIVATE conversation to anyone holding the
    /// URL, so the window has to close by itself — the previous behaviour was "public
    /// forever unless a human remembers to revoke", which is a disclosure waiting for
    /// someone to forget. A month covers the realistic reasons people share a transcript
    /// (send it to a colleague, cite it in a ticket, read it after a holiday) while
    /// guaranteeing an abandoned link stops resolving. Deliberately not 24 hours or 7
    /// days: re-sharing is cheap for the OWNER (one click, the create endpoint already
    /// rotates) but expensive for the RECIPIENT, who has to ask again — a very short
    /// default would train people into a re-issue habit that leaves more live URLs
    /// around, not fewer. Deliberately not a year: past a month nobody remembers which
    /// conversations they shared, which is the state this constant exists to prevent.</para>
    /// </summary>
    public const int DefaultTimeToLiveDays = 30;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatSessionId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>SHA-256 hex digest (64 chars) of the raw share token.</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null while the link is active; stamped on revocation or rotation.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// Hard deadline after which the link stops resolving, regardless of revocation.
    /// Non-nullable on purpose: NULL would have to mean "never expires", which is exactly
    /// the permanent-link hole this column exists to close, and a NOT NULL constraint is
    /// the only layer that can stop a future code path from re-opening it by omission.
    /// The initializer is a floor, not the real policy — <c>CreateShareLinkCommandHandler</c>
    /// overwrites it from <c>ShareLinkOptions.TimeToLive</c>.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(DefaultTimeToLiveDays);

    public ChatSession? ChatSession { get; set; }
}
