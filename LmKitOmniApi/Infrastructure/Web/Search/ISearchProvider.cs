namespace LmKitOmniApi.Infrastructure.Web.Search;

/// <summary>One normalized web-search hit (URL is absolute http(s), title/snippet plain text).</summary>
public sealed record WebSearchResult(string Url, string Title, string Snippet);

/// <summary>
/// A single web-search backend. Implementations are pure adapters (NO caching,
/// NO serialization): the composite <see cref="ResilientWebSearchService"/> owns
/// caching, ordering, fallback and the JSON wire contract. A provider returns an
/// empty list when the backend yields no hits and throws on transport/auth
/// failures so the composite can fall through to the next provider.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable provider id used in logs/metrics ("brave", "tavily", "duckduckgo").</summary>
    string Name { get; }

    /// <summary>Whether this provider is configured (e.g. an API key is present).</summary>
    bool IsConfigured { get; }

    /// <summary>Runs the search. Returns 0..count normalized hits; throws on failure.</summary>
    Task<List<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct);
}
