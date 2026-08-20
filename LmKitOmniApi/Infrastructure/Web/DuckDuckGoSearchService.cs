using System.Net.Http;
using System.Text.Json;
using HtmlAgilityPack;
using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Infrastructure.Web;

public class DuckDuckGoSearchService : IWebSearchService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DuckDuckGoSearchService> _logger;

    public DuckDuckGoSearchService(
        IHttpClientFactory httpClientFactory,
        ILogger<DuckDuckGoSearchService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<string> SearchWebAsync(string query, int count = 5)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        try
        {
            var response = await _httpClient.GetStringAsync(url);
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(response);

            var results = new List<object>();
            var nodes = htmlDoc.DocumentNode.SelectNodes("//a[@class='result__snippet']");

            if (nodes != null)
            {
                foreach (var node in nodes.Take(count))
                {
                    var href = node.GetAttributeValue("href", "");
                    var title = node.InnerText?.Trim();
                    
                    // The snippet is usually the inner text of the parent or next sibling in DDG HTML, 
                    // but for simplicity we'll just extract the snippet text.
                    results.Add(new {
                        url = href,
                        snippet = title
                    });
                }
            }

            return JsonSerializer.Serialize(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web search failed.");
            return "[Web search is temporarily unavailable.]";
        }
    }
}
