namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Result of a headless-browser fetch: the rendered page text (the agent-visible
/// observation, already capped) plus an OPTIONAL screenshot file id.
/// <see cref="ScreenshotFileId"/> is the on-disk (server-generated) id of a
/// persisted screenshot served by the file-download endpoint, mirroring
/// <see cref="ProducedFile.Id"/>. It is reserved for a future version: v1 is
/// text-only and always returns <c>null</c> here (see <see cref="IBrowserFetchExecutor"/>).
/// </summary>
public sealed record BrowserFetchResult(string Text, string? ScreenshotFileId)
{
    public static BrowserFetchResult TextOnly(string text) => new(text, null);
}

/// <summary>
/// Fetches and renders a single web page in an OS-isolated container sandbox and
/// returns its rendered text — a stateless, READ-ONLY slice of "computer-use"
/// (fetch + read), NOT interactive click/type automation.
///
/// Unlike <see cref="IPythonCodeExecutor"/> (which runs with <c>--network none</c>),
/// browsing REQUIRES network egress, so the container is networked and the
/// URL-level SSRF gate (<see cref="ToolSandboxService.ValidateUrlAsync"/>) plus the
/// optional host allowlist are the primary defense and MUST run before any browser
/// is launched. All other container hardening (non-root, dropped capabilities,
/// no-new-privileges, read-only rootfs, memory/cpu/pids/wall-clock limits) mirrors
/// the Python sandbox.
/// </summary>
public interface IBrowserFetchExecutor
{
    /// <summary>
    /// True only when the tool is enabled AND a browser image is configured. The
    /// orchestrator uses this to decide whether to offer the browse_web tool; when
    /// false, callers must not navigate and should surface the "not configured" path.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Fetches <paramref name="url"/> in the sandbox and returns the captured rendered
    /// text (capped). Never throws for fetch-level failures — a disabled tool, an
    /// empty/invalid URL, an SSRF-refused or non-allowlisted target, a timeout, a
    /// non-zero exit, or a launch failure all come back as a bracketed,
    /// agent-readable message (treated as untrusted tool output downstream). The
    /// (<paramref name="tenantId"/>, <paramref name="userId"/>) identity scopes any
    /// persisted artifact (reserved for a future screenshot version).
    /// </summary>
    Task<BrowserFetchResult> FetchAsync(string url, Guid tenantId, Guid userId, CancellationToken ct = default);
}
