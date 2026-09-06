using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Widget-key secret facade over <see cref="SecretTokens"/>, mirroring the house pattern in
/// <c>Application.ApiKeys.ApiKeySecret</c>: 32 cryptographically random bytes for the raw key
/// (base64url, no padding — exactly 43 chars) and a SHA-256 hex digest as the ONLY value ever
/// persisted (in <c>TenantWidgetSettings.WidgetApiKeyHash</c>). The raw key is shown once at
/// rotation and can never be recovered afterwards.
/// </summary>
public static class WidgetSecrets
{
    /// <summary>
    /// Upper bound on the presented <c>X-Widget-Key</c> header value before hashing.
    /// A real key is exactly 43 characters; this only rejects abusive inputs while
    /// keeping the lookup path identical for every plausible key.
    /// </summary>
    public const int MaxPresentedLength = 256;

    /// <summary>Header that carries the raw widget key on <c>POST /api/widget/auth</c>.</summary>
    public const string HeaderName = "X-Widget-Key";

    /// <summary>Generates a fresh raw widget key (43 chars, base64url alphabet).</summary>
    public static string Generate() => SecretTokens.GenerateUrlSafeToken();

    /// <summary>SHA-256 hex digest of a raw widget key — the stored/compared representation.</summary>
    public static string Hash(string rawKey) => SecretTokens.HashSha256Hex(rawKey);
}
