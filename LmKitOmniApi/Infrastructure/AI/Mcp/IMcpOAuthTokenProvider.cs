using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// Fetches (and caches) OAuth 2.0 bearer tokens for MCP servers configured with the
/// client-credentials grant (RFC 6749 §4.4). The returned token is injected as an
/// <c>Authorization: Bearer</c> header on outbound MCP requests.
/// </summary>
public interface IMcpOAuthTokenProvider
{
    /// <summary>
    /// Returns a valid access token for <paramref name="server"/>, fetching a fresh one
    /// only when the cache is empty or the cached token is within the refresh skew of
    /// expiry. Throws <see cref="InvalidOperationException"/> when the server is not
    /// configured for client-credentials, the token endpoint is blocked by the SSRF
    /// sandbox, or the token request fails.
    /// </summary>
    Task<string> GetAccessTokenAsync(ExternalMcpServer server, CancellationToken ct = default);
}
