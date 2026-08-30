using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Shared crypto for the URL/header-safe secret-token family
/// (<c>Application.Share.ShareLinkToken</c> and <c>Application.ApiKeys.ApiKeySecret</c>):
/// 32 cryptographically random bytes rendered as base64url (no padding — URL- and
/// header-safe by construction, exactly 43 chars) for the raw token, and a SHA-256
/// hex digest as the only value ever persisted. Distinct from
/// <see cref="RefreshTokenProtector"/>, which deliberately uses standard
/// (non-URL-safe) base64 for its cookie-borne token.
/// </summary>
public static class SecretTokens
{
    /// <summary>Generates a fresh raw token (43 chars, base64url alphabet, no padding).</summary>
    public static string GenerateUrlSafeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>SHA-256 hex digest of a raw token — the stored/compared representation.</summary>
    public static string HashSha256Hex(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
