using System.Net;
using LmKitOmniApi.Infrastructure.Web;
using LmKitOmniApi.Infrastructure.Web.Search;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit coverage for the composite web-search chain: provider ordering,
/// configuration gating, fallback on failure, and wire-format compatibility
/// with the legacy DuckDuckGo service.
/// </summary>
public sealed class SearchProviderTests
{
    private static IDistributedCache Cache() => new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static IConfiguration ConfigWith(string? braveKey = null, string? tavilyKey = null)
    {
        var values = new Dictionary<string, string?>();
        if (braveKey is not null) values["WebSearch:Brave:ApiKey"] = braveKey;
        if (tavilyKey is not null) values["WebSearch:Tavily:ApiKey"] = tavilyKey;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ResilientWebSearchService Build(params ISearchProvider[] providers) =>
        new(providers, Cache(), NullLogger<ResilientWebSearchService>.Instance);

    [Fact]
    public async Task NoConfiguredProviders_ReturnsNotConfiguredNotice()
    {
        var unconfiguredBrave = new BraveSearchProvider(new HttpClient(), ConfigWith());
        var service = Build(unconfiguredBrave);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("not configured", result);
    }

    [Fact]
    public async Task UnconfiguredProvider_IsSkipped_ConfiguredOneWins()
    {
        var brave = new StubProvider("brave", isConfigured: false, hits: [new("https://a.example/", "A", "a")]);
        var tavily = new StubProvider("tavily", isConfigured: true, hits: [new("https://b.example/", "B", "b")]);
        var service = Build(brave, tavily);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("b.example", result);
        Assert.Equal(0, brave.Calls);
        Assert.Equal(1, tavily.Calls);
    }

    [Fact]
    public async Task FirstProviderFailure_FallsThroughToSecond()
    {
        var failing = new StubProvider("brave", isConfigured: true, throws: new HttpRequestException("401"));
        var working = new StubProvider("duckduckgo", isConfigured: true, hits: [new("https://c.example/", "C", "c")]);
        var service = Build(failing, working);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("c.example", result);
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, working.Calls);
    }

    [Fact]
    public async Task EmptyHits_FallThroughToNextProvider()
    {
        var empty = new StubProvider("brave", isConfigured: true, hits: []);
        var populated = new StubProvider("tavily", isConfigured: true, hits: [new("https://d.example/", "D", "d")]);
        var service = Build(empty, populated);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("d.example", result);
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsUnavailableNotice()
    {
        var failing = new StubProvider("brave", isConfigured: true, throws: new HttpRequestException("boom"));
        var service = Build(failing);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("temporarily unavailable", result);
    }

    [Fact]
    public async Task WireFormat_MatchesLegacyShape_UrlTitleSnippet()
    {
        var provider = new StubProvider("brave", isConfigured: true, hits: [new("https://e.example/", "Title", "Snippet")]);
        var service = Build(provider);

        var result = await service.SearchWebAsync("test query");
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var first = doc.RootElement[0];
        Assert.Equal("https://e.example/", first.GetProperty("url").GetString());
        Assert.Equal("Title", first.GetProperty("title").GetString());
        Assert.Equal("Snippet", first.GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task InvalidQuery_ReturnsInvalidNotice_WithoutCallingProviders()
    {
        var provider = new StubProvider("brave", isConfigured: true, hits: [new("https://a.example/", "A", "a")]);
        var service = Build(provider);

        var tooLong = await service.SearchWebAsync(new string('q', 501));
        Assert.Contains("invalid", tooLong);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task BraveProvider_ParsesApiResponse()
    {
        var json = """
            {"web":{"results":[
              {"url":"https://example.com/one","title":"One","description":"first"},
              {"url":"https://example.com/two","title":"Two","description":"second"}
            ]}}
            """;
        var handler = new StubHttpHandler(json, HttpStatusCode.OK);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.search.brave.com") };
        var provider = new BraveSearchProvider(client, ConfigWith(braveKey: "key"));

        Assert.True(provider.IsConfigured);
        var hits = await provider.SearchAsync("q", 5, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://example.com/one", hits[0].Url);
        Assert.Equal("One", hits[0].Title);
        Assert.Equal("first", hits[0].Snippet);
    }

    [Fact]
    public async Task TavilyProvider_ParsesApiResponse()
    {
        var json = """
            {"results":[
              {"url":"https://example.com/x","title":"X","content":"content x"}
            ]}
            """;
        var handler = new StubHttpHandler(json, HttpStatusCode.OK);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com") };
        var provider = new TavilySearchProvider(client, ConfigWith(tavilyKey: "key"));

        Assert.True(provider.IsConfigured);
        var hits = await provider.SearchAsync("q", 5, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal("https://example.com/x", hits[0].Url);
        Assert.Equal("content x", hits[0].Snippet);
    }

    [Fact]
    public async Task SearxProvider_Unconfigured_IsSkipped()
    {
        var provider = new SearxSearchProvider(new HttpClient(), ConfigWith());
        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public async Task SearxProvider_ParsesApiResponse()
    {
        var json = """
            {"query":"q","results":[
              {"url":"https://example.com/searx1","title":"S1","content":"snippet 1"},
              {"url":"javascript:;","title":"bad","content":"dropped"},
              {"url":"https://example.com/searx2","title":"S2","content":"snippet 2"}
            ]}
            """;
        var handler = new StubHttpHandler(json, HttpStatusCode.OK);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://searxng:8080") };
        var provider = new SearxSearchProvider(client, ConfigWith());
        // IsConfigured reads WebSearch:Searx:BaseUrl — provide it via a custom config:
        var configured = new SearxSearchProvider(client, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WebSearch:Searx:BaseUrl"] = "http://searxng:8080" }).Build());

        Assert.True(configured.IsConfigured);
        Assert.False(provider.IsConfigured);

        var hits = await configured.SearchAsync("q", 5, CancellationToken.None);

        Assert.Equal(2, hits.Count); // javascript: URL is dropped
        Assert.Equal("https://example.com/searx1", hits[0].Url);
        Assert.Equal("S1", hits[0].Title);
        Assert.Equal("snippet 1", hits[0].Snippet);
    }

    /// <summary>
    /// LIVE check against the Compose SearXNG (skipped when it is not running).
    /// Proves the real instance serves the JSON format the provider needs.
    /// </summary>
    [SkippableFact]
    public async Task SearxProvider_LiveRealInstance_ReturnsResults()
    {
        var handler = new HttpClientHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var provider = new SearxSearchProvider(client, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WebSearch:Searx:BaseUrl"] = "http://localhost:8888" }).Build());

        try
        {
            var hits = await provider.SearchAsync("lm-kit omni agent", 5, CancellationToken.None);
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.StartsWith("http", h.Url));
        }
        catch (HttpRequestException ex)
        {
            Skip.If(true, $"SearXNG not reachable on localhost:8888 — skipping live check. ({ex.Message})");
        }
        catch (TaskCanceledException) when (!TaskCanceledException_CanceledByCaller())
        {
            Skip.If(true, "SearXNG did not answer in time — skipping live check.");
        }

        static bool TaskCanceledException_CanceledByCaller() => false;
    }

    /// <summary>
    /// LIVE end-to-end through the REAL production path: SearXNG container →
    /// SearxSearchProvider → ResilientWebSearchService (caching + fallback +
    /// legacy JSON wire format). This is exactly what the ReAct WEB_SEARCH tool
    /// and the content-creation pipeline consume. Skipped when the container
    /// is not running on localhost:8888.
    /// </summary>
    [SkippableFact]
    public async Task Searx_LiveThroughResilientService_EmitsLegacyWireJson()
    {
        using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WebSearch:Searx:BaseUrl"] = "http://localhost:8888" }).Build();
        var service = new ResilientWebSearchService(
            new[] { (ISearchProvider)new SearxSearchProvider(client, config) },
            Cache(),
            NullLogger<ResilientWebSearchService>.Instance);

        try
        {
            var json = await service.SearchWebAsync("aspire framework", count: 5);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            Assert.Equal(System.Text.Json.JsonValueKind.Object, first.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("url").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("title").GetString()));
        }
        catch (HttpRequestException ex)
        {
            Skip.If(true, $"SearXNG not reachable on localhost:8888 — skipping live check. ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            Skip.If(true, "SearXNG did not answer in time — skipping live check.");
        }
    }

    private sealed class StubProvider(string name, bool isConfigured, List<WebSearchResult>? hits = null, Exception? throws = null) : ISearchProvider
    {
        public int Calls { get; private set; }

        public string Name => name;
        public bool IsConfigured => isConfigured;

        public Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Calls++;
            if (throws is not null) throw throws;
            return Task.FromResult(hits ?? []);
        }
    }

    private sealed class StubHttpHandler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
