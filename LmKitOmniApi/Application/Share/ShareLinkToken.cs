using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LmKitOmniApi.Application.Share
{
    /// <summary>
    /// Share-token crypto, mirroring the refresh-token pattern in
    /// <c>Infrastructure.Security.RefreshTokenProtector</c>: 32 cryptographically
    /// random bytes for the raw token (base64url, no padding — URL-safe by
    /// construction) and a SHA-256 hex digest as the only value ever persisted.
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
        public static string Generate()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64Url.EncodeToString(bytes);
        }

        /// <summary>SHA-256 hex digest of a raw token — the stored/compared representation.</summary>
        public static string Hash(string token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }
    }
}
