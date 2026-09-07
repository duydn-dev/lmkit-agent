using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.Web;
using LmKitOmniApi.Infrastructure.Web.Search;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit coverage for the composite web-search chain: provider ordering,
/// configuration gating, fallback on failure, cache-key isolation between the
/// composite and the legacy scraper, and the <see cref="WebSearchOutcome"/>
/// contract (payload is always valid JSON; failures never masquerade as one).
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

    private static ResilientWebSearchService Build(IDistributedCache cache, params ISearchProvider[] providers) =>
        new(providers, cache, NullLogger<ResilientWebSearchService>.Instance);

    [Fact]
    public async Task NoConfiguredProviders_ReturnsNotConfiguredNotice()
    {
        var unconfiguredBrave = new BraveSearchProvider(new HttpClient(), ConfigWith());
        var service = Build(unconfiguredBrave);

        var result = await service.SearchWebAsync("test query");
        Assert.Equal(WebSearchStatus.NotConfigured, result.Status);
        Assert.Contains("not configured", result.Message);
        // The notice names every way to configure the chain, SearXNG included.
        Assert.Contains("WebSearch__Searx__BaseUrl", result.Message);
        Assert.Contains("WebSearch__Brave__ApiKey", result.Message);
        Assert.Contains("WebSearch__Tavily__ApiKey", result.Message);
    }

    [Fact]
    public async Task UnconfiguredProvider_IsSkipped_ConfiguredOneWins()
    {
        var brave = new StubProvider("brave", isConfigured: false, hits: [new("https://a.example/", "A", "a")]);
        var tavily = new StubProvider("tavily", isConfigured: true, hits: [new("https://b.example/", "B", "b")]);
        var service = Build(brave, tavily);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("b.example", result.ResultsJson);
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
        Assert.True(result.IsSuccess);
        Assert.Contains("c.example", result.ResultsJson);
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
        Assert.Contains("d.example", result.ResultsJson);
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsUnavailableNotice()
    {
        var failing = new StubProvider("brave", isConfigured: true, throws: new HttpRequestException("boom"));
        var service = Build(failing);

        var result = await service.SearchWebAsync("test query");
        Assert.Equal(WebSearchStatus.Unavailable, result.Status);
        Assert.Contains("temporarily unavailable", result.Message);
    }

    [Fact]
    public async Task AllProvidersEmpty_ReportsNoResults_NotUnavailable()
    {
        var empty = new StubProvider("brave", isConfigured: true, hits: []);
        var service = Build(empty);

        var result = await service.SearchWebAsync("test query");
        Assert.Equal(WebSearchStatus.NoResults, result.Status);
    }

    [Fact]
    public async Task WireFormat_MatchesLegacyShape_UrlTitleSnippet()
    {
        var provider = new StubProvider("brave", isConfigured: true, hits: [new("https://e.example/", "Title", "Snippet")]);
        var service = Build(provider);

        var result = await service.SearchWebAsync("test query");
        using var doc = System.Text.Json.JsonDocument.Parse(result.ResultsJson);
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
        Assert.Equal(WebSearchStatus.InvalidQuery, tooLong.Status);
        Assert.Contains("invalid", tooLong.Message);
        Assert.Equal(0, provider.Calls);
    }

    // --- T4: the contract that made the suite red -------------------------------

    /// <summary>
    /// THE regression this contract exists for. The old API returned
    /// <c>Task&lt;string&gt;</c> whose value was sometimes a JSON array and
    /// sometimes bracketed prose ("[Web search is temporarily unavailable.]").
    /// Because the prose starts with '[' it parsed as an array start and then blew
    /// up on the second character — every consumer had to guess. Now the payload
    /// slot is ALWAYS parseable JSON, whatever the status.
    /// </summary>
    [Theory]
    [InlineData(WebSearchStatus.Unavailable)]
    [InlineData(WebSearchStatus.NoResults)]
    [InlineData(WebSearchStatus.NotConfigured)]
    [InlineData(WebSearchStatus.InvalidQuery)]
    public async Task EveryFailureMode_StillYieldsParseableJsonArray(WebSearchStatus expected)
    {
        var service = expected switch
        {
            WebSearchStatus.Unavailable => Build(new StubProvider("brave", true, throws: new HttpRequestException("boom"))),
            WebSearchStatus.NoResults => Build(new StubProvider("brave", true, hits: [])),
            WebSearchStatus.NotConfigured => Build(new StubProvider("brave", isConfigured: false)),
            _ => Build(new StubProvider("brave", true, hits: [new("https://a.example/", "A", "a")]))
        };

        var query = expected is WebSearchStatus.InvalidQuery ? new string('q', 501) : "test query";
        var outcome = await service.SearchWebAsync(query);

        Assert.Equal(expected, outcome.Status);
        Assert.False(outcome.IsSuccess);
        Assert.Equal("[]", outcome.ResultsJson);

        // The whole point: this parse can never throw.
        using var doc = System.Text.Json.JsonDocument.Parse(outcome.ResultsJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Empty(doc.RootElement.EnumerateArray());

        // The human-readable notice lives out of band, and is only ever rendered
        // for a model — never handed to a parser.
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
        Assert.StartsWith("[", outcome.ToToolOutput());
    }

    [Fact]
    public async Task Success_ToToolOutput_IsTheRawJsonArray()
    {
        var service = Build(new StubProvider("brave", true, hits: [new("https://e.example/", "T", "S")]));
        var outcome = await service.SearchWebAsync("test query");

        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.Message);
        Assert.Equal(outcome.ResultsJson, outcome.ToToolOutput());
        using var doc = System.Text.Json.JsonDocument.Parse(outcome.ToToolOutput());
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    // --- T12: ordering ----------------------------------------------------------

    /// <summary>
    /// SearXNG is the documented FIRST link (self-hosted, no key, no quota) —
    /// see Program.cs, README and SearxSearchProvider. It used to fall into the
    /// catch-all bucket, tied with the DuckDuckGo scraper and BEHIND the paid
    /// APIs. Registration order must not matter; rank must.
    /// </summary>
    [Fact]
    public void ProviderOrder_IsSearxThenBraveThenTavilyThenDuckDuckGo()
    {
        var service = Build(
            new StubProvider("duckduckgo", true),
            new StubProvider("tavily", true),
            new StubProvider("brave", true),
            new StubProvider("searx", true));

        Assert.Equal(new[] { "searx", "brave", "tavily", "duckduckgo" }, service.ProviderOrder);
    }

    [Fact]
    public void ProviderOrder_UnknownProviderSitsAheadOfTheScraperOnly()
    {
        var service = Build(
            new StubProvider("duckduckgo", true),
            new StubProvider("experimental", true),
            new StubProvider("searx", true));

        Assert.Equal(new[] { "searx", "experimental", "duckduckgo" }, service.ProviderOrder);
    }

    [Fact]
    public async Task SearxIsTriedFirst_EvenWhenAPaidProviderIsRegisteredFirst()
    {
        var brave = new StubProvider("brave", isConfigured: true, hits: [new("https://brave.example/", "B", "b")]);
        var searx = new StubProvider("searx", isConfigured: true, hits: [new("https://searx.example/", "S", "s")]);
        var service = Build(brave, searx);

        var result = await service.SearchWebAsync("test query");
        Assert.Contains("searx.example", result.ResultsJson);
        Assert.Equal(1, searx.Calls);
        Assert.Equal(0, brave.Calls);
    }

    // --- T12: cache-key isolation ------------------------------------------------

    /// <summary>
    /// The composite and the legacy DuckDuckGo layer hash the SAME
    /// <c>{query}:{count}</c> tuple. With a shared key prefix, the scraper's
    /// cached payload was read back by the composite as its own successful
    /// result, bypassing every other provider for the whole TTL. The namespaces
    /// must differ.
    /// </summary>
    [Fact]
    public void CompositeAndLegacyCacheNamespaces_DoNotCollide()
    {
        Assert.NotEqual(ResilientWebSearchService.CacheKeyPrefix, DuckDuckGoSearchService.CacheKeyPrefix);
    }

    [Fact]
    public async Task LegacyCacheEntry_DoesNotPreemptTheCompositeChain()
    {
        var cache = Cache();
        // Poison the legacy namespace exactly as a throttled scrape used to.
        var poisoned = LegacyCacheKey("shared query", 5);
        await cache.SetStringAsync(poisoned, "[]");

        var searx = new StubProvider("searx", isConfigured: true, hits: [new("https://searx.example/", "S", "s")]);
        var service = Build(cache, searx);

        var result = await service.SearchWebAsync("shared query", count: 5);

        Assert.True(result.IsSuccess);
        Assert.Contains("searx.example", result.ResultsJson);
        Assert.Equal(1, searx.Calls);
    }

    [Fact]
    public async Task EmptyResult_IsNotCached_SoTheNextCallRetriesTheChain()
    {
        var cache = Cache();
        var empty = new StubProvider("searx", isConfigured: true, hits: []);
        var service = Build(cache, empty);

        await service.SearchWebAsync("dry query");
        await service.SearchWebAsync("dry query");

        Assert.Equal(2, empty.Calls); // a dry spell must not silence the chain for the TTL
    }

    /// <summary>A failed scrape must not leave "[]" behind for the next caller.</summary>
    [Fact]
    public async Task LegacyScraper_DoesNotCacheAnEmptyScrape()
    {
        var cache = Cache();
        var handler = new StubHttpHandler("<html><body>no results here</body></html>", HttpStatusCode.OK);
        var service = new DuckDuckGoSearchService(new HttpClient(handler), cache, NullLogger<DuckDuckGoSearchService>.Instance);

        var outcome = await service.SearchWebAsync("nothing matches", count: 5);

        Assert.Equal(WebSearchStatus.NoResults, outcome.Status);
        Assert.Equal("[]", outcome.ResultsJson);
        Assert.Null(await cache.GetStringAsync(LegacyCacheKey("nothing matches", 5)));
    }

    private static string LegacyCacheKey(string query, int count) =>
        DuckDuckGoSearchService.CacheKeyPrefix
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}:{count}")));

    // --- Provider adapters -------------------------------------------------------

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
    public void SearxProvider_Unconfigured_IsSkipped()
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
    /// The scraper adapter must surface a scrape FAILURE as an exception so the
    /// composite falls through, and an empty-but-successful scrape as no hits.
    /// </summary>
    [Fact]
    public async Task DuckDuckGoProvider_TranslatesLegacyOutcomeToProviderSemantics()
    {
        var failing = new DuckDuckGoSearchService(
            new HttpClient(new StubHttpHandler("nope", HttpStatusCode.ServiceUnavailable)),
            Cache(), NullLogger<DuckDuckGoSearchService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => new DuckDuckGoSearchProvider(failing).SearchAsync("q", 5, CancellationToken.None));

        var barren = new DuckDuckGoSearchService(
            new HttpClient(new StubHttpHandler("<html><body>nothing</body></html>", HttpStatusCode.OK)),
            Cache(), NullLogger<DuckDuckGoSearchService>.Instance);
        Assert.Empty(await new DuckDuckGoSearchProvider(barren).SearchAsync("q", 5, CancellationToken.None));
    }

    // --- LIVE checks (skipped unless SearXNG is actually running) -----------------

    private const string SearxLiveHost = "localhost";
    private const int SearxLivePort = 8888;

    /// <summary>
    /// Probes the Compose SearXNG with a plain TCP connect. This gate — rather
    /// than a try/catch around the search itself — is what makes the live tests
    /// genuinely skippable: the composite service deliberately CONVERTS transport
    /// failures into a status, so no <see cref="HttpRequestException"/> ever
    /// reaches a caller's catch block.
    /// </summary>
    private static async Task<bool> SearxIsReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var probe = new TcpClient();
            await probe.ConnectAsync(SearxLiveHost, SearxLivePort, cts.Token);
            return probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static IConfiguration SearxLiveConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebSearch:Searx:BaseUrl"] = $"http://{SearxLiveHost}:{SearxLivePort}"
        }).Build();

    /// <summary>
    /// LIVE check against the Compose SearXNG (skipped when it is not running).
    /// Proves the real instance serves the JSON format the provider needs.
    /// </summary>
    [SkippableFact]
    public async Task SearxProvider_LiveRealInstance_ReturnsResults()
    {
        Skip.IfNot(await SearxIsReachableAsync(),
            $"SearXNG is not listening on {SearxLiveHost}:{SearxLivePort} — skipping the live check.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var provider = new SearxSearchProvider(client, SearxLiveConfig());

        var hits = await provider.SearchAsync("lm-kit omni agent", 5, CancellationToken.None);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.StartsWith("http", h.Url));
    }

    /// <summary>
    /// LIVE end-to-end through the REAL production path: SearXNG container →
    /// SearxSearchProvider → ResilientWebSearchService (caching + fallback +
    /// legacy JSON wire format). This is exactly what the ReAct WEB_SEARCH tool
    /// and the content-creation pipeline consume. Skipped when the container is
    /// not running.
    /// </summary>
    [SkippableFact]
    public async Task Searx_LiveThroughResilientService_EmitsLegacyWireJson()
    {
        Skip.IfNot(await SearxIsReachableAsync(),
            $"SearXNG is not listening on {SearxLiveHost}:{SearxLivePort} — skipping the live check.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var service = new ResilientWebSearchService(
            new[] { (ISearchProvider)new SearxSearchProvider(client, SearxLiveConfig()) },
            Cache(),
            NullLogger<ResilientWebSearchService>.Instance);

        var outcome = await service.SearchWebAsync("aspire framework", count: 5);
        Skip.IfNot(outcome.IsSuccess, $"SearXNG answered but produced no usable results: {outcome.Message}");

        using var doc = System.Text.Json.JsonDocument.Parse(outcome.ResultsJson);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        Assert.Equal(System.Text.Json.JsonValueKind.Object, first.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("url").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("title").GetString()));
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
