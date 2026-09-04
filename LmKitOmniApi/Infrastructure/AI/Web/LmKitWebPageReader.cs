using LMKit.Agents.Tools.BuiltIn.Net;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// The one real implementation of <see cref="IWebPageReader"/>: a thin wrapper over
/// LM-Kit.NET 2026.8.6's built-in <see cref="WebReadTool"/>. Reading a live page needs
/// the network, so this type is LIVE-ONLY and is never exercised in CI (tests inject a
/// fake <see cref="IWebPageReader"/>).
///
/// The whole egress policy is server-side and never model-facing: the model only ever
/// supplies the URL. The <see cref="WebEgressPolicy"/> built here runs in
/// <see cref="WebEgressPolicy.EgressMode.PublicWeb"/> mode with no intranet exceptions,
/// so loopback / RFC1918 / link-local (cloud metadata) / CGNAT / ULA / multicast /
/// reserved addresses are unreachable by construction, redirects are followed manually
/// with every hop re-validated, and connections are DNS-pinned (a name cannot
/// re-resolve to a private address between check and connect).
/// </summary>
public sealed class LmKitWebPageReader : IWebPageReader
{
    private readonly WebReadOptions _options;

    public LmKitWebPageReader(IOptions<WebReadOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> ReadAsync(string url, CancellationToken ct = default)
    {
        // Server-side egress gate: public-web only, no allowed/private host exceptions.
        var policy = new WebEgressPolicy
        {
            Mode = WebEgressPolicy.EgressMode.PublicWeb,
            MaxRedirects = _options.MaxRedirects,
            MaxResponseBytes = _options.MaxResponseBytes,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            UserAgent = _options.UserAgent,
        };

        var tool = new WebReadTool(new WebReadTool.Options
        {
            Egress = policy,
            MaxContentChars = _options.MaxContentChars,
        });

        // WebReadTool's model-facing schema is the URL alone; InvokeAsync returns the
        // extracted Markdown. A refused hop surfaces as InvalidOperationException with
        // the gate's own reason — the service turns that into an agent-readable message.
        return await tool.InvokeAsync(url, ct);
    }
}
