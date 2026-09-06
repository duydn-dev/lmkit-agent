using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.Web.Search;

/// <summary>
/// SearXNG self-hosted metasearch adapter (GET {BaseUrl}/search?format=json).
/// The first link in the provider chain when <c>WebSearch:Searx:BaseUrl</c> is
/// configured — no API key, no per-request quota, runs next to the API in
/// Compose. The SearXNG instance MUST allow the JSON format in its
/// <c>settings.yml</c> (search.formats: [html, json]) or the API answers 403;
/// the bundled searxng/settings.yml does exactly that.
/// </summary>
public sealed class SearxSearchProvider(HttpClient httpClient, IConfiguration configuration) : ISearchProvider
{
    public string Name => "searx";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["WebSearch:Searx:BaseUrl"]);

    public async Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var baseUrl = configuration["WebSearch:Searx:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("SearXNG base URL is not configured (WebSearch:Searx:BaseUrl).");

        count = Math.Clamp(count, 1, 30);
        var url = $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("LmKitOmniAgent/1.0");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<WebSearchResult>();
        if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= count) break;
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
