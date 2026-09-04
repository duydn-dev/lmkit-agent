namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// One numbered interactive element from an accessibility-grounded observation. The
/// model addresses actions at <see cref="Ref"/> (preferred over raw coordinates).
/// </summary>
public sealed record InteractiveElement(int Ref, string Role, string Name, string? Value);

/// <summary>
/// The result of one executed step: what the page looks like now. Mirrors an
/// accessibility snapshot — a screenshot PLUS a numbered list of interactive elements —
/// so the next decision can be grounded in element refs, not pixel guessing.
/// <see cref="ScreenshotFileId"/> is the owner-scoped, server-generated id of the
/// persisted screenshot (served by <c>GET /api/files/{id}</c>), exactly like
/// <c>ProducedFile.Id</c> / <c>BrowserFetchResult.ScreenshotFileId</c>. When a step
/// fails, <see cref="Error"/> is set and the other fields carry whatever was still
/// observable (often nothing).
/// </summary>
public sealed record ComputerUseObservation
{
    public string? ScreenshotFileId { get; init; }
    public IReadOnlyList<InteractiveElement> Elements { get; init; } = Array.Empty<InteractiveElement>();
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Error { get; init; }

    public bool IsError => !string.IsNullOrEmpty(Error);

    public static ComputerUseObservation Failed(string error) => new() { Error = error };
}

/// <summary>
/// Runs a single interactive browser step inside an OS-isolated container via the
/// injectable <see cref="Security.IProcessRunner"/> seam, returning the resulting
/// <see cref="ComputerUseObservation"/>. Isolation mirrors the read-only browse tool
/// (<c>BrowserFetchExecutor</c>): non-root, cap-drop ALL, no-new-privileges, read-only
/// rootfs, memory/cpu/pids/wall-clock caps.
///
/// Because a browser needs network egress, the container is NOT run with
/// <c>--network none</c>; instead every <c>navigate</c> is SSRF-validated
/// (<c>ToolSandboxService.ValidateUrlAsync</c>) and checked against the EXPLICIT
/// <see cref="ComputerUseOptions.AllowedHosts"/> allowlist BEFORE any container launches.
/// Both guards constrain only the initial navigation target — in-page redirects and
/// subresources are not re-vetted by the host, so operators should also pin the
/// container to an egress-restricted network via <see cref="ComputerUseOptions.NetworkName"/>.
/// </summary>
public interface IComputerUseExecutor
{
    /// <summary>True only when the tool is enabled AND a browser image is configured.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Applies <paramref name="action"/> against the persistent browser profile mounted
    /// from <paramref name="sessionDirectory"/> and returns the new observation. Never
    /// throws for step-level failures — a disabled tool, a refused/over-limit navigation,
    /// a timeout, a non-zero exit, or a launch failure all come back as an observation
    /// with <see cref="ComputerUseObservation.Error"/> set (no exception leaks to the
    /// agent). Any screenshot is persisted under the (<paramref name="tenantId"/>,
    /// <paramref name="userId"/>) upload root and surfaced as
    /// <see cref="ComputerUseObservation.ScreenshotFileId"/>.
    /// </summary>
    Task<ComputerUseObservation> StepAsync(
        ComputerUseAction action,
        Guid tenantId,
        Guid userId,
        string sessionDirectory,
        CancellationToken ct = default);
}
