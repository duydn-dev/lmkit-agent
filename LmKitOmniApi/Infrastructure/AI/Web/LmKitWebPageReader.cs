using LMKit.Agents.Tools.BuiltIn.Net;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// The one real implementation of <see cref="IWebPageReader"/>: LM-Kit.NET 2026.9.0's built-in
/// <see cref="WebReadTool"/> as the primary reader, with a gated cookie-challenge retry behind it.
/// Reading a live page needs the network, so this type is LIVE-ONLY and is never exercised in CI
/// (tests inject a fake <see cref="IWebPageReader"/>); the two pieces of logic it composes —
/// <see cref="WebCookieChallenge"/> and <see cref="WebPageTextExtractor"/> — are pure and are
/// covered by CI against real captured pages.
///
/// <para>
/// The whole egress policy is server-side and never model-facing: the model only ever supplies the
/// URL. The <see cref="WebEgressPolicy"/> built here runs in
/// <see cref="WebEgressPolicy.EgressMode.PublicWeb"/> mode with no intranet exceptions, so loopback
/// / RFC1918 / link-local (cloud metadata) / CGNAT / ULA / multicast / reserved addresses are
/// unreachable by construction, redirects are followed manually with every hop re-validated, and
/// connections are DNS-pinned (a name cannot re-resolve to a private address between check and
/// connect). <b>Every</b> request this type makes — including the challenge retry — is built from
/// that same policy through <see cref="WebEgressFetcher"/>, so the retry inherits the identical
/// SSRF gate and DNS pinning rather than reaching around it.
/// </para>
///
/// <para>
/// <b>Why the retry exists.</b> <see cref="WebReadTool"/> has no cookie jar, which is the right
/// default — except for the sites that answer every cookieless GET with a JavaScript stub that sets
/// a cookie and reloads (laodong.vn does exactly this). There the tool sees 177 bytes of script
/// instead of a 111 KB article and the user is told the link could not be read. The retry is
/// deliberately one-shot and narrow: <see cref="WebCookieChallenge.TryExtractCookie"/> has to
/// recognize the stub first, and the cookie is only attached when the URL that answered is the same
/// host that was asked for, so a redirect can never push a cookie to a third party.
/// </para>
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
        var policy = CreatePolicy();

        string? primary = null;
        Exception? primaryFailure = null;

        try
        {
            // WebReadTool's model-facing schema is the URL alone; InvokeAsync returns the extracted
            // Markdown. A refused hop surfaces as InvalidOperationException with the gate's own
            // reason — the service turns that into an agent-readable message.
            primary = await ReadWithToolAsync(url, policy, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Kept, not swallowed: when the retry cannot do better either, this is the exception the
            // caller (and therefore the agent) has always seen.
            primaryFailure = ex;
        }

        // The happy path: real content that is not a challenge stub. Nothing extra is fetched.
        var challengeCookie = WebCookieChallenge.TryExtractCookie(primary);
        if (!string.IsNullOrWhiteSpace(primary) && challengeCookie is null)
            return primary;

        var retried = await TryReadThroughChallengeAsync(url, policy, challengeCookie, ct);
        if (!string.IsNullOrWhiteSpace(retried))
            return retried;

        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        return primary ?? string.Empty;
    }

    /// <summary>
    /// One gated fetch, plus a single cookie-bearing retry when that response turns out to be a
    /// JavaScript cookie challenge. Returns <see langword="null"/> when the fetch yields nothing
    /// readable, so the caller can fall back to the primary reader's own failure.
    /// </summary>
    private async Task<string?> TryReadThroughChallengeAsync(
        string url, WebEgressPolicy policy, string? knownChallengeCookie, CancellationToken ct)
    {
        var fetcher = new WebEgressFetcher(policy);
        var first = await fetcher.FetchAsync(url, ct);

        // Decode once, then inspect the RAW text: the challenge is a <script> body, and the
        // extractor below strips scripts, so detection has to happen before extraction.
        var firstRaw = WebPageTextExtractor.DecodeBody(first.Body, first.ContentType);
        var cookie = knownChallengeCookie ?? WebCookieChallenge.TryExtractCookie(firstRaw);
        if (cookie is null)
            return WebPageTextExtractor.Extract(firstRaw);

        if (!IsSameHost(url, first.FinalUrl))
            return null;

        var retry = new WebEgressFetcher(policy);
        retry.RequestHeaders["Cookie"] = cookie;

        var answered = await retry.FetchAsync(url, ct);
        return WebPageTextExtractor.Extract(answered.Body, answered.ContentType);
    }

    private async Task<string> ReadWithToolAsync(string url, WebEgressPolicy policy, CancellationToken ct)
    {
        var tool = new WebReadTool(new WebReadTool.Options
        {
            Egress = policy,
            MaxContentChars = _options.MaxContentChars,
        });

        return await tool.InvokeAsync(url, ct);
    }

    /// <summary>
    /// Server-side egress gate: public-web only, no allowed/private host exceptions. Reused
    /// verbatim by the retry so both fetches are gated identically.
    /// </summary>
    private WebEgressPolicy CreatePolicy() => new()
    {
        Mode = WebEgressPolicy.EgressMode.PublicWeb,
        MaxRedirects = _options.MaxRedirects,
        MaxResponseBytes = _options.MaxResponseBytes,
        Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        UserAgent = _options.UserAgent,
    };

    /// <summary>
    /// A challenge cookie is scoped to the host that issued it. The compare is on the host the
    /// policy actually landed on (<c>FinalUrl</c>, after every validated hop), so a redirect chain
    /// that ends somewhere else never receives it.
    /// </summary>
    private static bool IsSameHost(string requestedUrl, Uri? finalUrl)
    {
        if (finalUrl is null)
            return true;

        return Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested)
            && string.Equals(requested.Host, finalUrl.Host, StringComparison.OrdinalIgnoreCase);
    }
}
