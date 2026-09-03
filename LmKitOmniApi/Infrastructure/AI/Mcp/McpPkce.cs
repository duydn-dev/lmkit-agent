using System.Security.Cryptography;
using System.Text;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// PKCE (RFC 7636) helpers plus a CSPRNG state generator for the OAuth 2.0
/// authorization-code flow. All values are URL-safe base64 (no padding), the encoding
/// RFC 7636 §4.1 mandates for the code verifier and challenge.
/// </summary>
public static class McpPkce
{
    /// <summary>
    /// A high-entropy code verifier: 32 random bytes → 43 base64url characters, within
    /// the RFC 7636 43–128 character range.
    /// </summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The S256 code challenge for a verifier: <c>BASE64URL(SHA256(ASCII(verifier)))</c>
    /// (RFC 7636 §4.2).
    /// </summary>
    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>An unguessable CSRF <c>state</c> value (256 bits of entropy).</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
