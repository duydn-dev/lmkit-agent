using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LmKitOmniApi.Application.ApiKeys;

/// <summary>
/// API-key secret crypto, mirroring the house pattern in
/// <c>Application.Share.ShareLinkToken</c> / <c>Infrastructure.Security.RefreshTokenProtector</c>:
/// 32 cryptographically random bytes for the raw key (base64url, no padding — header-safe
/// by construction, exactly 43 chars) and a SHA-256 hex digest as the ONLY value ever
/// persisted (in <c>TenantApiKey.ApiKey</c>). The raw key is shown once at creation and
/// can never be recovered afterwards.
/// </summary>
public static class ApiKeySecret
{
    /// <summary>
    /// Upper bound on the presented <c>X-Api-Key</c> header value before hashing.
    /// A real key is exactly 43 characters; this only rejects abusive inputs while
    /// keeping the lookup path identical for every plausible key.
    /// </summary>
    public const int MaxPresentedLength = 256;

    /// <summary>Generates a fresh raw API key (43 chars, base64url alphabet).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>SHA-256 hex digest of a raw key — the stored/compared representation.</summary>
    public static string Hash(string rawKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
    }
}
