using System.Net.Http.Headers;
using System.Text.Json;
using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Infrastructure.Web.Search;

/// <summary>
/// Brave Search API adapter (https://api.search.brave.com/res/v1/web/search).
/// Requires <c>WebSearch:Brave:ApiKey</c>. Maps the subset of the response we
/// consume (url/title/description) and clamps the result count server-side to
/// the API's 1..20 range.
/// </summary>
public sealed class BraveSearchProvider(HttpClient httpClient, IConfiguration configuration) : ISearchProvider
{
    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";

    public string Name => "brave";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["WebSearch:Brave:ApiKey"]);

    public async Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var apiKey = configuration["WebSearch:Brave:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Brave Search API key is not configured.");

        count = Math.Clamp(count, 1, 20);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.UserAgent.ParseAdd("LmKitOmniAgent/1.0");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<WebSearchResult>();
        if (!doc.RootElement.TryGetProperty("web", out var web)
            || web.ValueKind != JsonValueKind.Object
            || !web.TryGetProperty("results", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rawUrl = item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            if (rawUrl is null || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) continue;

            var title = item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() ?? string.Empty : string.Empty;
            var snippet = item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String ? descEl.GetString() ?? string.Empty : string.Empty;
            results.Add(new WebSearchResult(uri.AbsoluteUri, title.Trim(), snippet.Trim()));
        }
        return results;
    }
}

/// <summary>
/// Tavily Search API adapter (https://api.tavily.com/search, POST JSON).
/// Requires <c>WebSearch:Tavily:ApiKey</c>. Requests include_content=false —
/// snippets only; deeper fetch/verify is the ResearchContentFetcher's job.
/// </summary>
public sealed class TavilySearchProvider(HttpClient httpClient, IConfiguration configuration) : ISearchProvider
{
    private const string Endpoint = "https://api.tavily.com/search";

    public string Name => "tavily";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["WebSearch:Tavily:ApiKey"]);

    public async Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var apiKey = configuration["WebSearch:Tavily:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Tavily API key is not configured.");

        var payload = JsonSerializer.Serialize(new
        {
            api_key = apiKey,
            query,
            max_results = Math.Clamp(count, 1, 20),
            include_answer = false,
            include_raw_content = false,
            include_domains = Array.Empty<string>(),
            exclude_domains = Array.Empty<string>()
        });

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(Endpoint, content, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<WebSearchResult>();
        if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rawUrl = item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            if (rawUrl is null || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) continue;

            var title = item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() ?? string.Empty : string.Empty;
            var snippet = item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String ? contentEl.GetString() ?? string.Empty : string.Empty;
            results.Add(new WebSearchResult(uri.AbsoluteUri, title.Trim(), snippet.Trim()));
        }
        return results;
    }
}

/// <summary>
/// DuckDuckGo HTML-scraper adapter — the legacy path, demoted to LAST fallback.
/// Behavior is unchanged from <see cref="DuckDuckGoSearchService.SearchWebAsync"/>
/// (same endpoint, same parsing); it exists here so a deployment with no API
/// keys keeps working. Unstable by nature (HTML scraping), hence its priority.
/// </summary>
public sealed class DuckDuckGoSearchProvider : ISearchProvider
{
    private readonly DuckDuckGoSearchService _legacy;

    public DuckDuckGoSearchProvider(DuckDuckGoSearchService legacy) => _legacy = legacy;

    public string Name => "duckduckgo";
    public bool IsConfigured => true;

    public async Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var outcome = await _legacy.SearchWebAsync(query, count, ct);
        var results = new List<WebSearchResult>();

        // A scraper failure must LOOK like a failure to the composite so it falls
        // through to nothing-was-found rather than pretending the chain succeeded.
        if (outcome.Status is WebSearchStatus.Unavailable)
            throw new HttpRequestException(outcome.Message ?? "DuckDuckGo scrape failed.");
        if (!outcome.IsSuccess) return results;

        // ResultsJson is contractually a JSON array — no defensive try/catch needed.
        using var doc = JsonDocument.Parse(outcome.ResultsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var url = item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            if (url is null) continue;
            var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
            var snippet = item.TryGetProperty("snippet", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
            results.Add(new WebSearchResult(url, title, snippet));
        }
        return results;
    }
}
