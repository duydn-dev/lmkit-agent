using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Share
{
    /// <summary>
    /// Share-token facade over <see cref="SecretTokens"/>: 32 cryptographically
    /// random bytes for the raw token (base64url, no padding — URL-safe by
    /// construction) and a SHA-256 hex digest as the only value ever persisted.
    /// Mirrors the refresh-token pattern in
    /// <c>Infrastructure.Security.RefreshTokenProtector</c>.
    /// </summary>
    public static class ShareLinkToken
    {
        /// <summary>
        /// Upper bound on presented-token length before hashing. A real token is
        /// exactly 43 characters; this only rejects abusive inputs while keeping the
        /// lookup path identical for every plausible token.
        /// </summary>
        public const int MaxPresentedLength = 128;

        /// <summary>Generates a fresh raw share token (43 chars, base64url alphabet).</summary>
        public static string Generate() => SecretTokens.GenerateUrlSafeToken();

        /// <summary>SHA-256 hex digest of a raw token — the stored/compared representation.</summary>
        public static string Hash(string token) => SecretTokens.HashSha256Hex(token);
    }
}
