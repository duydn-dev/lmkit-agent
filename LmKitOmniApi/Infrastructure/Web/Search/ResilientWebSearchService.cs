using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Infrastructure.Web.Search;

/// <summary>
/// Composite <see cref="IWebSearchService"/>: tries providers in the documented
/// order (SearXNG → Brave → Tavily → DuckDuckGo scraper, skipping unconfigured
/// ones), caches the winner's payload for 5 minutes, and logs per-provider
/// failures.
/// <para>
/// The hit payload is byte-compatible with the legacy DuckDuckGo wire format
/// (<c>[{url,title,snippet}]</c>), but it is now carried by
/// <see cref="WebSearchOutcome"/> so that a failure NEVER travels as bracketed
/// prose inside the JSON slot — see the contract note on that type.
/// </para>
/// </summary>
public sealed class ResilientWebSearchService(
    IEnumerable<ISearchProvider> providers,
    IDistributedCache cache,
    ILogger<ResilientWebSearchService> logger) : IWebSearchService
{
    /// <summary>
    /// Cache-key namespace for the COMPOSITE result. Deliberately distinct from
    /// <see cref="DuckDuckGoSearchService"/>'s own namespace: the two layers hash
    /// the same <c>{query}:{count}</c> tuple, so a shared prefix let the inner
    /// scraper's cache entry be read back as if it were a composite result —
    /// which short-circuited every other provider for the full TTL.
    /// </summary>
    internal const string CacheKeyPrefix = "web-search:composite:";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly List<ISearchProvider> _providers = providers
        .Where(p => p.IsConfigured)
        .OrderBy(Rank)
        .ToList();

    /// <summary>
    /// Provider precedence. Self-hosted SearXNG first (no API key, no quota, runs
    /// beside the API in Compose), then the paid APIs, then the HTML scraper last
    /// because it is the least reliable. Matches <c>Program.cs</c>, the README and
    /// <see cref="SearxSearchProvider"/>'s own documentation.
    /// </summary>
    internal static int Rank(ISearchProvider provider) => provider.Name switch
    {
        "searx" => 0,
        "brave" => 1,
        "tavily" => 2,
        "duckduckgo" => 4,
        _ => 3
    };

    /// <summary>The configured providers in the order they will be attempted (diagnostics/tests).</summary>
    internal IReadOnlyList<string> ProviderOrder => _providers.Select(p => p.Name).ToList();

    public async Task<WebSearchOutcome> SearchWebAsync(string query, int count = 5, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 500)
            return WebSearchOutcome.Empty(WebSearchStatus.InvalidQuery, "Web search query is invalid.");
        count = Math.Clamp(count, 1, 10);

        var cacheKey = CacheKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}:{count}")));
        var cached = await cache.GetStringAsync(cacheKey, ct);
        // Only non-empty successes are ever written, so a hit is always a success.
        if (cached is not null) return WebSearchOutcome.Success(cached);

        if (_providers.Count == 0)
            return WebSearchOutcome.Empty(
                WebSearchStatus.NotConfigured,
                "Web search is not configured: set WebSearch__Searx__BaseUrl (self-hosted, no API key), "
                + "WebSearch__Brave__ApiKey or WebSearch__Tavily__ApiKey.");

        var anyProviderAnswered = false;
        foreach (var provider in _providers)
        {
            try
            {
                var hits = await provider.SearchAsync(query, count, ct);
                anyProviderAnswered = true;
                if (hits.Count == 0)
                {
                    logger.LogInformation("Web search provider {Provider} returned no hits for the query; trying next.", provider.Name);
                    continue;
                }

                var serialized = JsonSerializer.Serialize(hits.Select(h => new { url = h.Url, title = h.Title, snippet = h.Snippet }));
                await cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheTtl
                }, ct);
                return WebSearchOutcome.Success(serialized);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Web search provider {Provider} failed; falling through to the next provider.", provider.Name);
            }
        }

        // Empty results are NOT cached: a transient dry spell must not silence the
        // whole chain for the TTL.
        return anyProviderAnswered
            ? WebSearchOutcome.Empty(WebSearchStatus.NoResults, "Web search returned no results.")
            : WebSearchOutcome.Empty(WebSearchStatus.Unavailable, "Web search is temporarily unavailable.");
    }
}
