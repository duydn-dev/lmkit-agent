namespace LmKitOmniApi.Infrastructure.AI.Research;

/// <summary>
/// Request body for <c>POST /api/research</c>. The contract is fixed — the
/// frontend is built against it in parallel: <c>query</c> is required (≤ 500
/// chars), <c>maxSources</c> is optional and clamped to 2..5 (default 3).
/// </summary>
public sealed class StartResearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int? MaxSources { get; set; }
}

/// <summary>
/// One successfully fetched, sanitized web source used by the synthesis pass.
/// <see cref="Content"/> is readable text only (scripts/styles/nav stripped,
/// whitespace collapsed) and already capped at
/// <see cref="ResearchLimits.MaxExtractedCharsPerSource"/>.
/// </summary>
public sealed record ResearchSource(string Url, string Title, string Content);

/// <summary>
/// Shape of one result element in the JSON array returned by
/// <see cref="LmKitOmniApi.Application.Abstractions.IWebSearchService"/>
/// (DuckDuckGo): <c>[{"url":"...","title":"...","snippet":"..."}]</c>.
/// </summary>
public sealed class WebSearchHit
{
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Snippet { get; set; }
}

/// <summary>Hard caps and budgets for the deep-research pipeline.</summary>
public static class ResearchLimits
{
    /// <summary>Maximum characters accepted for the research query.</summary>
    public const int MaxQueryChars = 500;

    /// <summary>Lower clamp for the requested source count.</summary>
    public const int MinSources = 2;

    /// <summary>Upper clamp for the requested source count.</summary>
    public const int MaxSources = 5;

    /// <summary>Default source count when the request omits it.</summary>
    public const int DefaultSources = 3;

    /// <summary>Maximum sub-questions produced by the decomposition pass.</summary>
    public const int MaxSubQuestions = 3;

    /// <summary>Top result URLs taken per sub-question from web search.</summary>
    public const int MaxUrlsPerSubQuestion = 3;

    /// <summary>Absolute cap on URL fetch attempts per research run (3 × 3).</summary>
    public const int MaxTotalFetchAttempts = 9;

    /// <summary>Maximum raw bytes read from any single fetched page (512 KB).</summary>
    public const int MaxContentBytes = 512 * 1024;

    /// <summary>Maximum readable characters kept per source after extraction.</summary>
    public const int MaxExtractedCharsPerSource = 8_000;

    /// <summary>
    /// Defensive cap on the combined source text fed to the synthesis prompt so
    /// a small local model's context is never blown even at 5 × 8,000-char
    /// sources; the per-source share is
    /// <c>min(MaxExtractedCharsPerSource, MaxSynthesisContextChars / sourceCount)</c>.
    /// </summary>
    public const int MaxSynthesisContextChars = 18_000;

    /// <summary>Completion-token cap for the synthesis pass.</summary>
    public const int SynthesisMaxCompletionTokens = 2_048;

    /// <summary>Completion-token cap for the decomposition pass.</summary>
    public const int DecomposeMaxCompletionTokens = 512;

    /// <summary>Overall wall-clock budget for one research run.</summary>
    public static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(120);
}
