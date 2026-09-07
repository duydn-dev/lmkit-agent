namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Why a web search ended the way it did. Travels OUT OF BAND from the result
/// payload so a caller never has to sniff prose out of a JSON string.
/// </summary>
public enum WebSearchStatus
{
    /// <summary>At least one provider returned at least one hit.</summary>
    Success,

    /// <summary>The query was empty or longer than the 500-character cap; no provider was called.</summary>
    InvalidQuery,

    /// <summary>No search provider is configured/enabled in this deployment.</summary>
    NotConfigured,

    /// <summary>Providers ran and answered, but nobody had a hit for this query.</summary>
    NoResults,

    /// <summary>Every configured provider failed (transport, auth, rate limit, parse).</summary>
    Unavailable
}

/// <summary>
/// The result of one web search.
/// <para>
/// CONTRACT: <see cref="ResultsJson"/> is ALWAYS a valid JSON array of
/// <c>{"url","title","snippet"}</c> objects — <c>"[]"</c> when there is nothing
/// to return, for ANY reason. It never carries a human-readable notice. That is
/// the whole point of this type: the old <c>Task&lt;string&gt;</c> contract
/// returned bracketed prose such as <c>"[Web search is temporarily
/// unavailable.]"</c>, which starts with <c>'['</c> and therefore masquerades as
/// a JSON array until a parser chokes on the second character.
/// </para>
/// <para>
/// Status/diagnostics live in <see cref="Status"/> and <see cref="Message"/>.
/// Callers that hand the result to a language model should use
/// <see cref="ToToolOutput"/>, which renders the notice for failures and the raw
/// JSON for successes.
/// </para>
/// </summary>
public sealed record WebSearchOutcome
{
    private WebSearchOutcome(WebSearchStatus status, string resultsJson, string? message)
    {
        Status = status;
        ResultsJson = resultsJson;
        Message = message;
    }

    /// <summary>Why the search ended as it did.</summary>
    public WebSearchStatus Status { get; }

    /// <summary>Always-parseable JSON array of hits; <c>"[]"</c> when there are none.</summary>
    public string ResultsJson { get; }

    /// <summary>Human-readable explanation for a non-success status; <c>null</c> on success.</summary>
    public string? Message { get; }

    /// <summary>True when at least one hit was returned.</summary>
    public bool IsSuccess => Status is WebSearchStatus.Success;

    /// <summary>A successful search carrying a non-empty JSON array of hits.</summary>
    public static WebSearchOutcome Success(string resultsJson) =>
        new(WebSearchStatus.Success, resultsJson, null);

    /// <summary>
    /// A search that produced no hits — for any reason. <see cref="ResultsJson"/>
    /// is <c>"[]"</c>, so JSON consumers can parse it unconditionally.
    /// </summary>
    public static WebSearchOutcome Empty(WebSearchStatus status, string message) =>
        new(status, "[]", message);

    /// <summary>
    /// Agent/tool-facing rendering: the JSON array on success, otherwise the
    /// bracketed notice the ReAct loop and the pipeline agents already expect.
    /// Only ever fed to a model — never to a JSON parser.
    /// </summary>
    public string ToToolOutput() => IsSuccess ? ResultsJson : $"[{Message}]";
}

/// <summary>
/// Web search for agents/tools. Implementations MUST honor the
/// <see cref="WebSearchOutcome"/> contract: the payload is always valid JSON,
/// failures are reported through the status, never smuggled into the payload.
/// </summary>
public interface IWebSearchService
{
    Task<WebSearchOutcome> SearchWebAsync(string query, int count = 5, CancellationToken ct = default);
}
