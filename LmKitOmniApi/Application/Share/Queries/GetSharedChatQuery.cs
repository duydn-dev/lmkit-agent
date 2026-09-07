using MediatR;
using System;
using System.Collections.Generic;

namespace LmKitOmniApi.Application.Share.Queries
{
    /// <summary>
    /// Resolves a presented share token to its public read-only transcript, or to the
    /// reason it will not resolve. See <see cref="SharedChatStatus"/> for exactly how
    /// much the answer is allowed to reveal.
    /// </summary>
    public class GetSharedChatQuery : IRequest<SharedChatResult>
    {
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>
    /// Why a share token did or did not resolve.
    ///
    /// <para><b>What changed and why.</b> This used to be a plain nullable DTO: unknown,
    /// revoked and orphaned tokens all produced the same bare 404, on the reasoning that
    /// the endpoint must not be an oracle for token state. Adding a deadline makes that
    /// blanket silence untenable — "hết hạn" and "đã thu hồi" are different events with
    /// different remedies (ask for a new link, versus you were deliberately cut off), and
    /// collapsing both into "không tìm thấy" leaves the recipient unable to tell either
    /// from a typo in the URL.</para>
    ///
    /// <para><b>What is still hidden.</b> <see cref="NotFound"/> stays opaque: a token
    /// that never existed, one that is syntactically absurd, and one whose session was
    /// deleted are all the same answer, so nothing here helps enumerate links. The
    /// enumeration risk that the old blanket 404 guarded against does not survive
    /// contact with the numbers anyway — the raw token is 32 cryptographically random
    /// bytes (2^256), and the endpoint is additionally per-IP rate-limited by
    /// <c>SharePolicy</c>. Nobody reaches a "revoked" or "expired" answer by guessing;
    /// the only party who can reach one is someone already holding a genuine URL, who by
    /// construction already knows the link existed. Telling THAT person which of the two
    /// things happened costs nothing and is the entire point.</para>
    /// </summary>
    public enum SharedChatStatus
    {
        /// <summary>Token resolved; <see cref="SharedChatResult.Chat"/> is populated.</summary>
        Ok = 0,

        /// <summary>
        /// Unknown token, malformed token, or a link whose session no longer exists.
        /// Deliberately one indistinguishable bucket.
        /// </summary>
        NotFound = 1,

        /// <summary>The owner pulled the link (explicitly, or by rotating it).</summary>
        Revoked = 2,

        /// <summary>The link outlived <c>ChatShareLink.ExpiresAtUtc</c>.</summary>
        Expired = 3
    }

    /// <summary>
    /// Outcome of a share-token lookup. Every non-<see cref="SharedChatStatus.Ok"/> value
    /// carries a null <see cref="Chat"/>: the two refusals differ only in what they SAY,
    /// never in what they hand over.
    /// </summary>
    public sealed class SharedChatResult
    {
        public SharedChatStatus Status { get; init; }

        /// <summary>Populated only when <see cref="Status"/> is <see cref="SharedChatStatus.Ok"/>.</summary>
        public SharedChatDto? Chat { get; init; }

        /// <summary>
        /// When the link stopped resolving — the revocation stamp or the deadline,
        /// depending on <see cref="Status"/>. Null for <see cref="SharedChatStatus.Ok"/>
        /// and <see cref="SharedChatStatus.NotFound"/>; a timestamp for an unknown token
        /// would be a fabrication, and there is nothing to date for a live one.
        /// </summary>
        public DateTime? RefusedAtUtc { get; init; }

        public static readonly SharedChatResult NotFound = new() { Status = SharedChatStatus.NotFound };

        public static SharedChatResult Ok(SharedChatDto chat) =>
            new() { Status = SharedChatStatus.Ok, Chat = chat };

        public static SharedChatResult Revoked(DateTime revokedAtUtc) =>
            new() { Status = SharedChatStatus.Revoked, RefusedAtUtc = revokedAtUtc };

        public static SharedChatResult Expired(DateTime expiredAtUtc) =>
            new() { Status = SharedChatStatus.Expired, RefusedAtUtc = expiredAtUtc };
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
