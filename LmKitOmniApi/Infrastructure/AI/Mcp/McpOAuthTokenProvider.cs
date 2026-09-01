using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// OAuth 2.0 client-credentials token provider for MCP servers. Tokens are cached in
/// process keyed by the server id plus a hash of its OAuth configuration, so any change
/// to the endpoint, client id, scopes, or secret transparently invalidates the entry.
///
/// Two independent SSRF defenses guard the token endpoint: a pre-flight
/// <see cref="ToolSandboxService.ValidateUrlAsync"/> DNS check, and the connect-time
/// re-vetting <see cref="SsrfSafeConnect"/> callback on the named "MCP-OAuth" HttpClient
/// (see Program.cs). The client secret is decrypted only in memory, immediately before
/// the request, and is never logged.
/// </summary>
public sealed class McpOAuthTokenProvider : IMcpOAuthTokenProvider
{
    public const string HttpClientName = "MCP-OAuth";
    public const string ClientCredentialsMode = "ClientCredentials";

    // Refresh a little before the server-stated expiry to avoid using a token that
    // expires mid-flight; fall back to a conservative lifetime when expires_in is absent.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, CachedToken> Cache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ToolSandboxService _sandbox;
    private readonly McpHeaderProtector _protector;
    private readonly TimeProvider _clock;
    private readonly ILogger<McpOAuthTokenProvider> _logger;

    public McpOAuthTokenProvider(
        IHttpClientFactory httpClientFactory,
        ToolSandboxService sandbox,
        McpHeaderProtector protector,
        TimeProvider clock,
        ILogger<McpOAuthTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sandbox = sandbox;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(ExternalMcpServer server, CancellationToken ct = default)
    {
        if (!string.Equals(server.AuthMode, ClientCredentialsMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MCP server '{server.Name}' is not configured for OAuth client-credentials.");
        if (string.IsNullOrWhiteSpace(server.OAuthTokenUrl) || string.IsNullOrWhiteSpace(server.OAuthClientId) ||
            string.IsNullOrWhiteSpace(server.OAuthClientSecretProtected))
            throw new InvalidOperationException($"MCP server '{server.Name}' is missing OAuth client-credentials configuration.");

        var cacheKey = BuildCacheKey(server);
        if (TryGetFresh(cacheKey, out var cached)) return cached;

        var gate = KeyLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Double-check: another caller may have refreshed while we waited.
            if (TryGetFresh(cacheKey, out cached)) return cached;

            var token = await RequestTokenAsync(server, ct);
            Cache[cacheKey] = token;
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string cacheKey, out string token)
    {
        if (Cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAtUtc - RefreshSkew > _clock.GetUtcNow())
        {
            token = entry.AccessToken;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private async Task<CachedToken> RequestTokenAsync(ExternalMcpServer server, CancellationToken ct)
    {
        var tokenUrl = server.OAuthTokenUrl!;
        var validation = await _sandbox.ValidateUrlAsync(tokenUrl, ct);
        if (!validation.IsAllowed)
            throw new InvalidOperationException(validation.DenialReason ?? "OAuth token endpoint was blocked by the URL sandbox.");

        var secret = _protector.Unprotect(server.OAuthClientSecretProtected!);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", server.OAuthClientId!),
            new("client_secret", secret)
        };
        if (!string.IsNullOrWhiteSpace(server.OAuthScopes))
            form.Add(new("scope", server.OAuthScopes.Trim()));

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("🔗 [MCP-OAuth] Token request to '{Server}' failed with {Status}", server.Name, (int)response.StatusCode);
            throw new InvalidOperationException($"OAuth token endpoint returned {(int)response.StatusCode}.");
        }

        var (accessToken, lifetime) = ParseTokenResponse(body);
        _logger.LogInformation("🔗 [MCP-OAuth] Obtained a bearer token for '{Server}' (expires in {Seconds:n0}s)", server.Name, lifetime.TotalSeconds);
        return new CachedToken(accessToken, _clock.GetUtcNow() + lifetime);
    }

    private static (string AccessToken, TimeSpan Lifetime) ParseTokenResponse(string body)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("OAuth token endpoint returned a non-JSON response.");
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("access_token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
            throw new InvalidOperationException("OAuth token response did not contain an access_token.");

        var lifetime = DefaultLifetime;
        if (root.TryGetProperty("expires_in", out var expires))
        {
            long? seconds = expires.ValueKind switch
            {
                JsonValueKind.Number when expires.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(expires.GetString(), out var s) => s,
                _ => null
            };
            if (seconds is > 0) lifetime = TimeSpan.FromSeconds(seconds.Value);
        }

        return (tokenElement.GetString()!, lifetime);
    }

    private static string BuildCacheKey(ExternalMcpServer server)
    {
        var material = string.Join('|', server.OAuthTokenUrl, server.OAuthClientId, server.OAuthScopes, server.OAuthClientSecretProtected);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"{server.Id:N}:{hash}";
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
}
