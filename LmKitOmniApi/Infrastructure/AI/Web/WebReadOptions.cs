namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Configuration for the native web fetch-and-read tool (<c>fetch_web</c> / the
/// <c>WEB_FETCH</c> action), which wraps LM-Kit.NET's built-in
/// <c>LMKit.Agents.Tools.BuiltIn.Net.WebReadTool</c>. Bound from the "WebRead"
/// configuration section.
///
/// DISABLED BY DEFAULT: fetching arbitrary URLs is outbound network egress, so it
/// only runs when an operator explicitly enables it. When disabled the tool is never
/// offered to the agent and any invocation returns a safe "not enabled" message —
/// same gating shape as the Python and browser sandboxes.
///
/// SSRF note: the LM-Kit <c>WebEgressPolicy</c> the reader builds from these settings
/// runs in <c>PublicWeb</c> mode, so the host's own network (loopback, RFC1918,
/// link-local + cloud-metadata, CGNAT, ULA, multicast, reserved) is unreachable by
/// construction, redirects are followed manually with every hop re-validated, and
/// connections are DNS-pinned. On top of that the service runs a pre-flight
/// <see cref="Security.ToolSandboxService.ValidateUrlAsync"/> before any fetch, for
/// defense-in-depth.
/// </summary>
public sealed class WebReadOptions
{
    public const string SectionName = "WebRead";

    /// <summary>Master switch. False (default) = the fetch_web tool is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Cap on the extracted text returned to the agent, in characters (a page lands in
    /// the model context, so it is chat-sized). Applied both as the LM-Kit
    /// <c>WebReadTool.Options.MaxContentChars</c> and again by the service as
    /// defense-in-depth. 0 = no cap (not recommended).
    /// </summary>
    public int MaxContentChars { get; set; } = 8_000;

    /// <summary>Hard cap on the (decompressed) response body the egress fetch will read, in bytes.</summary>
    public long MaxResponseBytes { get; set; } = 5_000_000;

    /// <summary>Most redirect hops one fetch may follow; each hop re-enters the egress validation.</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Wall-clock budget for the whole fetch, redirects included (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>User-Agent presented by the gate-driven fetch.</summary>
    public string UserAgent { get; set; } = "LmKitOmniApi-WebRead/1.0";
}
