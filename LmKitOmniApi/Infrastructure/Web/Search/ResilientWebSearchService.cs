using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Infrastructure.Web.Search;

/// <summary>
/// Composite <see cref="IWebSearchService"/>: tries providers in configured
/// order (Brave → Tavily → DuckDuckGo scraper, skipping unconfigured ones),
/// caches the winner's payload for 5 minutes (unchanged cache behavior), and
/// logs per-provider failures. The JSON wire contract is byte-compatible with
/// the legacy DuckDuckGo service (<c>[{url,title,snippet}]</c>), so every
/// consumer (research agent, deep research, content pipeline, web-search tool)
/// keeps working unchanged.
/// </summary>
public sealed class ResilientWebSearchService(
    IEnumerable<ISearchProvider> providers,
    IDistributedCache cache,
    ILogger<ResilientWebSearchService> logger) : IWebSearchService
{
    private readonly List<ISearchProvider> _providers = providers
        .Where(p => p.IsConfigured)
        .OrderBy(p => p.Name switch { "brave" => 0, "tavily" => 1, _ => 2 })
        .ToList();

    public async Task<string> SearchWebAsync(string query, int count = 5, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 500) return "[Web search query is invalid.]";
        count = Math.Clamp(count, 1, 10);

        var cacheKey = $"web-search:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}:{count}")))}";
        var cached = await cache.GetStringAsync(cacheKey, ct);
        if (cached is not null) return cached;

        if (_providers.Count == 0)
            return "[Web search is not configured: set WebSearch__Brave__ApiKey or WebSearch__Tavily__ApiKey.]";

        foreach (var provider in _providers)
        {
            try
            {
                var hits = await provider.SearchAsync(query, count, ct);
                if (hits.Count == 0)
                {
                    logger.LogInformation("Web search provider {Provider} returned no hits for the query; trying next.", provider.Name);
                    continue;
                }

                var serialized = JsonSerializer.Serialize(hits.Select(h => new { url = h.Url, title = h.Title, snippet = h.Snippet }));
                await cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                }, ct);
                return serialized;
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

        return "[Web search is temporarily unavailable.]";
    }
}
