using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Application.Share;

/// <summary>
/// Configuration for the public share-link deadline, bound from the "ShareLinks"
/// configuration section.
///
/// <para><b>What this bounds.</b> A <see cref="ChatShareLink"/> used to have no deadline
/// at all: every read path checked only <c>RevokedAtUtc == null</c>, so a link minted
/// once stayed publicly resolvable forever unless a human remembered to revoke it. The
/// resource behind it is a whole private conversation, so "forever" is the wrong default
/// by a wide margin. These options put a clock on it.</para>
///
/// <para><b>Why there is no per-link TTL.</b> The create endpoint
/// (<c>POST /api/share/chat-sessions/{sessionId}</c>) takes no request body, the client's
/// share button sends none, and <see cref="Commands.CreateShareLinkCommand"/> carries only
/// the identity triple. Adding a caller-chosen lifetime would mean inventing a parameter
/// nothing sends and a UI nobody asked for — and a caller-chosen TTL is also the one knob
/// that can be turned back to "effectively forever", which would reinstate the hole per
/// link. The lifetime is therefore an OPERATOR setting: one number, uniform per
/// deployment, auditable in configuration.</para>
///
/// <para><b>Not a sweeper.</b> Unlike <c>ApprovalExpiryOptions</c> there is no background
/// worker here and no <c>Enabled</c> flag. The deadline is stamped on the row at creation
/// and enforced on every read by <c>GetSharedChatQueryHandler</c>, so there is nothing to
/// turn off and nothing that has to run for an expired link to stop resolving. Reaping
/// long-dead rows is a housekeeping concern, not a security one, and is left out
/// deliberately: the row is also the audit trail for "this conversation was shared".</para>
/// </summary>
public sealed class ShareLinkOptions
{
    public const string SectionName = "ShareLinks";

    /// <summary>
    /// How long a freshly minted share link resolves. Defaults to
    /// <see cref="ChatShareLink.DefaultTimeToLiveDays"/> so this option and the entity's
    /// own fallback can never disagree. Out-of-range values are clamped by
    /// <see cref="TimeToLive"/> rather than rejected, because a typo in configuration
    /// must not be able to take the API down at startup — nor to produce a link that is
    /// already dead (0) or effectively permanent (1_000_000).
    /// </summary>
    public int TimeToLiveDays { get; set; } = ChatShareLink.DefaultTimeToLiveDays;

    /// <summary>
    /// Validated lifetime for a newly created link. Clamped to [1, 365]: a sub-day link
    /// cannot survive the recipient's next working day, and anything past a year is
    /// "permanent" wearing a timestamp.
    /// </summary>
    public TimeSpan TimeToLive => TimeSpan.FromDays(Math.Clamp(TimeToLiveDays, 1, 365));
}

/// <summary>Single-call registration for share-link configuration.</summary>
public static class ShareLinkServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="ShareLinkOptions"/> from the "ShareLinks" section.
    ///
    /// <para>Safe to omit entirely — in the test host, or in a deployment whose
    /// <c>Program.cs</c> has not been updated. <c>IOptions&lt;ShareLinkOptions&gt;</c>
    /// still resolves from the default options infrastructure, yielding
    /// <see cref="ChatShareLink.DefaultTimeToLiveDays"/>. Skipping this call costs the
    /// ability to TUNE the deadline; it can never remove the deadline.</para>
    /// </summary>
    public static IServiceCollection AddShareLinks(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ShareLinkOptions>(configuration.GetSection(ShareLinkOptions.SectionName));
        return services;
    }
}
