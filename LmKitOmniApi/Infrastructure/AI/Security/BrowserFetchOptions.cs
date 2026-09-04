namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Configuration for the container-backed headless-browser BROWSE tool. Bound from
/// the "BrowserTool" configuration section. DISABLED BY DEFAULT: a headless browser
/// is a networked, egress-capable component (a genuine slice of "computer-use"), so
/// it only runs when an operator explicitly enables it AND provisions a hardened
/// headless-Chromium container image reachable from the API. When disabled, the
/// browse_web tool is never offered to the agent and any invocation returns a safe
/// "not configured" message.
///
/// SSRF note — this is NOT the no-network Python sandbox. Because browsing requires
/// network egress the container is NOT launched with <c>--network none</c>, so the
/// URL-level SSRF gate (<see cref="ToolSandboxService.ValidateUrlAsync"/>, which
/// vets the host AND every resolved IP against private/loopback/metadata ranges)
/// plus the optional <see cref="AllowedHosts"/> allowlist are the primary defense.
/// They constrain only the INITIAL navigation target; redirects and subresources
/// loaded by the page inside the container are a wider surface that the host cannot
/// re-vet, so operators should additionally run the browser container on an
/// egress-restricted network (firewall / proxy) for defense in depth.
/// </summary>
public sealed class BrowserFetchOptions
{
    public const string SectionName = "BrowserTool";

    /// <summary>Master switch. False (default) = the browse tool is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Container image with a headless browser that renders a URL passed as its
    /// single argument and writes the rendered page text to stdout, e.g. a hardened
    /// Chromium "--headless --dump-dom --no-sandbox" image.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Container runtime executable (default "docker").</summary>
    public string RuntimePath { get; set; } = "docker";

    /// <summary>Hard wall-clock limit per fetch (seconds). Browsers are slower than a
    /// bare interpreter, so this defaults higher than the Python sandbox.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Memory ceiling passed to the runtime (MB). A browser needs more than a
    /// bare interpreter, so this defaults higher than the Python sandbox.</summary>
    public int MemoryMb { get; set; } = 512;

    /// <summary>CPU quota passed to the runtime (e.g. 1.0 = one core).</summary>
    public double Cpus { get; set; } = 1.0;

    /// <summary>Max characters of rendered page text returned to the agent (capped so a
    /// large page can never flood the model context).</summary>
    public int MaxOutputChars { get; set; } = 12_000;

    /// <summary>
    /// Optional egress allowlist of permitted destination hostnames. When non-empty a
    /// URL whose host is not listed is refused BEFORE the browser launches. Mirrors
    /// <see cref="DatabaseAgentOptions.AllowedHosts"/>. Internal/loopback/link-local/
    /// metadata targets are ALWAYS blocked regardless of this list (see the SSRF gate).
    /// Note: this only constrains the initial navigation host, not in-page redirects or
    /// subresources.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = new();
}
