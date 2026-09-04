namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Native web fetch-and-read tool: retrieves ONE public web page and returns its main
/// content as clean, length-capped text with a source citation — the "read + verify +
/// cite" step after web search (which only names pages / scrapes snippets). Wraps
/// LM-Kit.NET's built-in <c>WebReadTool</c> / <c>WebEgressPolicy</c> behind a
/// restrictive, public-web-only egress policy, and runs a pre-flight
/// <see cref="Security.ToolSandboxService.ValidateUrlAsync"/> SSRF check before any
/// fetch for defense-in-depth.
/// </summary>
public interface IWebReadService
{
    /// <summary>
    /// True only when an operator has enabled the tool (WebRead:Enabled). The
    /// orchestrator uses this to decide whether to offer the <c>fetch_web</c> tool;
    /// when false, <see cref="ReadAsync"/> refuses without fetching.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Fetches and reads <paramref name="query"/>. The query is the URL, optionally
    /// <c>"url|what-to-extract"</c> — only the URL portion is fetched (the trailing
    /// instruction is context for the agent, never sent to the fetcher). Never throws
    /// for fetch-level failures: a disabled tool, an empty/invalid/over-long URL, an
    /// SSRF-refused target, or a load error all come back as a bracketed,
    /// agent-readable message (treated as untrusted tool output downstream). On success
    /// the extracted text is length-capped and prefixed with a <c>Nguồn: &lt;url&gt;</c>
    /// citation line.
    /// </summary>
    Task<string> ReadAsync(string query, CancellationToken ct = default);
}
