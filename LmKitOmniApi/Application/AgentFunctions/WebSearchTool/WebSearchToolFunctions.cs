using System.ComponentModel;
using LMKit.Agents.Tools;
using HtmlAgilityPack;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Application.AgentFunctions.WebSearchTool;

public class WebSearchToolFunctions
{
    private static readonly HttpClient _httpClient;

    static WebSearchToolFunctions()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,vi;q=0.8");
    }

    [LMFunction("SearchWeb", "Searches the web for the given query and returns top results with URLs and snippets. Use this to find up-to-date information, news, or URLs.")]
    public string SearchWeb([Description("The search query string.")] string query)
    {
        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return $"Error: Cannot search web right now (HTTP {response.StatusCode}).";

            var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]");
            if (results == null || results.Count == 0)
                return "No results found.";

            var output = new System.Text.StringBuilder();
            int count = 0;

            foreach (var result in results)
            {
                if (count >= 5) break;

                var titleNode = result.SelectSingleNode(".//a[@class='result__url']");
                var snippetNode = result.SelectSingleNode(".//a[@class='result__snippet']");

                if (titleNode != null && snippetNode != null)
                {
                    var title = HtmlEntity.DeEntitize(titleNode.InnerText).Trim();
                    var link = titleNode.GetAttributeValue("href", "").Trim();
                    
                    if (link.Contains("uddg="))
                    {
                        var start = link.IndexOf("uddg=") + 5;
                        var end = link.IndexOf('&', start);
                        if (end == -1) end = link.Length;
                        link = Uri.UnescapeDataString(link.Substring(start, end - start));
                    }

                    var snippet = HtmlEntity.DeEntitize(snippetNode.InnerText).Trim();
                    
                    output.AppendLine($"Result {count + 1}:");
                    output.AppendLine($"Title: {title}");
                    output.AppendLine($"URL: {link}");
                    output.AppendLine($"Snippet: {snippet}");
                    output.AppendLine();
                    count++;
                }
            }

            return output.ToString();
        }
        catch (Exception ex)
        {
            return $"Error occurred while searching: {ex.Message}";
        }
    }

    [LMFunction("ReadWebPage", "Reads the main content of a specific URL. Use this to get detailed information from a webpage returned by SearchWeb.")]
    public string ReadWebPage([Description("The full URL of the webpage to read.")] string url)
    {
        try
        {
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return $"Error: Cannot read webpage (HTTP {response.StatusCode}).";

            var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var nodesToRemove = doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //header | //aside");
            if (nodesToRemove != null)
            {
                foreach (var node in nodesToRemove) node.Remove();
            }

            var body = doc.DocumentNode.SelectSingleNode("//body");
            if (body == null) return "No body found in webpage.";

            var text = HtmlEntity.DeEntitize(body.InnerText);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (text.Length > 4000)
                text = text.Substring(0, 4000) + "... [CONTENT TRUNCATED]";

            return text;
        }
        catch (Exception ex)
        {
            return $"Error occurred while reading webpage: {ex.Message}";
        }
    }
}
