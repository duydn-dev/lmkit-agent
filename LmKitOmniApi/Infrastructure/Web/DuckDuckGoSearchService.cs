using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Infrastructure.Web;

/// <summary>
/// Legacy DuckDuckGo HTML-scraper search. Reached only through
/// <see cref="Search.DuckDuckGoSearchProvider"/> as the LAST link of the
/// composite chain. Honors the <see cref="WebSearchOutcome"/> contract: hits are
/// always valid JSON, failures are reported by status.
/// </summary>
public class DuckDuckGoSearchService : IWebSearchService
{
    /// <summary>
    /// Cache-key namespace for THIS layer only. It must stay distinct from
    /// <see cref="Search.ResilientWebSearchService.CacheKeyPrefix"/>: both layers
    /// hash the same <c>{query}:{count}</c> tuple, so a shared prefix made the
    /// composite read this scraper's entry back as its own result.
    /// </summary>
    internal const string CacheKeyPrefix = "web-search:duckduckgo:";

    private readonly HttpClient _httpClient;
    private readonly ILogger<DuckDuckGoSearchService> _logger;
    private readonly IDistributedCache _cache;

    public DuckDuckGoSearchService(
        HttpClient httpClient,
        IDistributedCache cache,
        ILogger<DuckDuckGoSearchService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<WebSearchOutcome> SearchWebAsync(string query, int count = 5, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 500)
            return WebSearchOutcome.Empty(WebSearchStatus.InvalidQuery, "Web search query is invalid.");
        count = Math.Clamp(count, 1, 10);
        var cacheKey = CacheKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}:{count}")));
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null) return WebSearchOutcome.Success(cached);

        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        try
        {
            using var responseMessage = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            responseMessage.EnsureSuccessStatusCode();
            var response = await responseMessage.Content.ReadAsStringAsync(ct);
            if (response.Length > 2_000_000)
                return WebSearchOutcome.Empty(WebSearchStatus.Unavailable, "Web search response exceeded the safety limit.");
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(response);

            var results = new List<object>();
            var nodes = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class,'result__a')]");

            if (nodes != null)
            {
                foreach (var node in nodes.Take(count))
                {
                    var href = NormalizeResultUrl(node.GetAttributeValue("href", ""));
                    if (href is null) continue;
                    var title = HtmlEntity.DeEntitize(node.InnerText).Trim();
                    var snippetNode = node.ParentNode?.ParentNode?
                        .SelectSingleNode(".//*[contains(@class,'result__snippet')]");
                    var snippet = HtmlEntity.DeEntitize(snippetNode?.InnerText ?? string.Empty).Trim();
                    results.Add(new {
                        url = href,
                        title,
                        snippet
                    });
                }
            }

            // An empty scrape is NOT cached: DuckDuckGo silently serves zero
            // results when it throttles us, and caching that would blind the
            // caller (and, historically, the whole composite chain) for the TTL.
            if (results.Count == 0)
                return WebSearchOutcome.Empty(WebSearchStatus.NoResults, "Web search returned no results.");

            var serialized = JsonSerializer.Serialize(results);
            await _cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            }, ct);
            return WebSearchOutcome.Success(serialized);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web search failed.");
            return WebSearchOutcome.Empty(WebSearchStatus.Unavailable, "Web search is temporarily unavailable.");
        }
    }

    private static string? NormalizeResultUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var encodedTarget = System.Web.HttpUtility.ParseQueryString(uri.Query)["uddg"];
            if (encodedTarget is null || !Uri.TryCreate(encodedTarget, UriKind.Absolute, out uri)) return null;
        }

        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
