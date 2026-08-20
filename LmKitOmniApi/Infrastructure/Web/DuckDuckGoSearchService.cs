using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Infrastructure.Web;

public class DuckDuckGoSearchService : IWebSearchService
{
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

    public async Task<string> SearchWebAsync(string query, int count = 5, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 500) return "[Web search query is invalid.]";
        count = Math.Clamp(count, 1, 10);
        var cacheKey = $"web-search:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}:{count}")))}";
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null) return cached;

        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        try
        {
            using var responseMessage = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            responseMessage.EnsureSuccessStatusCode();
            var response = await responseMessage.Content.ReadAsStringAsync(ct);
            if (response.Length > 2_000_000) return "[Web search response exceeded the safety limit.]";
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

            var serialized = JsonSerializer.Serialize(results);
            await _cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            }, ct);
            return serialized;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web search failed.");
            return "[Web search is temporarily unavailable.]";
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
