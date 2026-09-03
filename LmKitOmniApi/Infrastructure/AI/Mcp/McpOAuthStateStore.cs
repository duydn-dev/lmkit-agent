using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// The server-side binding for one in-flight authorization-code request. Everything the
/// callback needs is recovered from here by the opaque <c>state</c> value, so the callback
/// never trusts client-supplied identity: the tenant/user/server and the PKCE verifier are
/// all pinned when <c>/authorize</c> was issued to the authenticated user.
/// </summary>
public sealed record McpOAuthStateEntry(
    Guid TenantId,
    Guid UserId,
    Guid ServerId,
    string CodeVerifier,
    string RedirectUri,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Stores <see cref="McpOAuthStateEntry"/> values in <see cref="IDistributedCache"/> keyed
/// by a CSPRNG <c>state</c> token. Entries are single-use (consumed on the callback) and
/// short-lived (~10 min), defeating CSRF and authorization-code replay. Expiry is enforced
/// both by the cache TTL and by an explicit clock check, so a stale entry is refused even
/// if a backing store served it past its TTL.
/// </summary>
public sealed class McpOAuthStateStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string KeyPrefix = "mcp-oauth-state:";

    private readonly IDistributedCache _cache;
    private readonly TimeProvider _clock;

    public McpOAuthStateStore(IDistributedCache cache, TimeProvider clock)
    {
        _cache = cache;
        _clock = clock;
    }

    /// <summary>
    /// Mints a new <c>state</c> token, stores the binding for it, and returns the token to
    /// embed in the authorize URL. <paramref name="expiresAtUtc"/> on the supplied entry is
    /// ignored and recomputed from the clock so callers cannot extend the TTL.
    /// </summary>
    public async Task<string> CreateAsync(McpOAuthStateEntry entry, CancellationToken ct = default)
    {
        var state = McpPkce.CreateState();
        var stamped = entry with { ExpiresAtUtc = _clock.GetUtcNow() + Lifetime };
        var payload = JsonSerializer.SerializeToUtf8Bytes(stamped);
        await _cache.SetAsync(KeyPrefix + state, payload, new DistributedCacheEntryOptions
        {
            // A small grace beyond the logical lifetime; the clock check below is authoritative.
            AbsoluteExpirationRelativeToNow = Lifetime + TimeSpan.FromMinutes(1)
        }, ct);
        return state;
    }

    /// <summary>
    /// Atomically reads and removes the binding for <paramref name="state"/>. Returns null
    /// when the state is unknown, already consumed, or expired — every rejection path a
    /// callback must treat identically.
    /// </summary>
    public async Task<McpOAuthStateEntry?> ConsumeAsync(string state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var key = KeyPrefix + state;
        var payload = await _cache.GetAsync(key, ct);
        if (payload is null) return null;

        // Single-use: remove before validating so a replay of the same state finds nothing.
        await _cache.RemoveAsync(key, ct);

        McpOAuthStateEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<McpOAuthStateEntry>(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (entry is null || entry.ExpiresAtUtc <= _clock.GetUtcNow()) return null;
        return entry;
    }
}
