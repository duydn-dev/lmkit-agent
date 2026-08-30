using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LmKitOmniApi.Infrastructure.AI.Research;

/// <summary>
/// SSRF-safe web page fetcher for the deep-research pipeline.
///
/// Safety model (per URL):
/// 1. <see cref="IResearchUrlValidator"/> (production: the shared
///    <c>ToolSandboxService.ValidateUrlAsync</c> gate) MUST allow the URL before
///    any request is sent — http/https only, private/loopback/link-local/
///    metadata hosts rejected, DNS results re-vetted.
/// 2. The typed <see cref="HttpClient"/> is registered with
///    <c>AllowAutoRedirect = false</c>, so a 3xx is treated as a failed fetch —
///    a redirect target never bypasses validation.
/// 3. Only <c>text/html</c> and <c>text/plain</c> responses are accepted.
/// 4. At most <see cref="ResearchLimits.MaxContentBytes"/> (512 KB) are read
///    from the body regardless of Content-Length — enforced here by a capped
///    manual read (the client's MaxResponseContentBufferSize is belt-and-braces).
/// 5. Readable text is extracted with HtmlAgilityPack (script/style/nav/footer
///    etc. stripped), whitespace collapsed, and capped at
///    <see cref="ResearchLimits.MaxExtractedCharsPerSource"/> (8,000 chars).
///
/// Every per-URL failure is logged and surfaced as <c>null</c> — never fatal to
/// the research run.
/// </summary>
public sealed class ResearchContentFetcher
{
    private const int MaxTitleChars = 200;
    private const int ReadBufferBytes = 16 * 1024;

    /// <summary>Elements whose text is never useful research content.</summary>
    private const string StrippedNodesXPath =
        "//script|//style|//noscript|//nav|//footer|//header|//aside|//form|//iframe|//svg|//template";

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private readonly HttpClient _httpClient;
    private readonly IResearchUrlValidator _urlValidator;
    private readonly ILogger<ResearchContentFetcher> _logger;

    public ResearchContentFetcher(
        HttpClient httpClient,
        IResearchUrlValidator urlValidator,
        ILogger<ResearchContentFetcher> logger)
    {
        _httpClient = httpClient;
        _urlValidator = urlValidator;
        _logger = logger;
    }

    /// <summary>
    /// Fetches one URL and returns its readable text, or <c>null</c> when the
    /// URL fails SSRF validation, the response is not successful text/html or
    /// text/plain, or extraction yields nothing useful. Only cancellation of
    /// <paramref name="ct"/> propagates; every other failure is logged + skipped.
    /// </summary>
    public async Task<ResearchSource?> FetchAsync(string url, CancellationToken ct = default)
    {
        // ── 1. SSRF gate — ALWAYS first, before any socket is opened ──
        try
        {
            var validation = await _urlValidator.ValidateAsync(url, ct);
            if (!validation.IsAllowed)
            {
                _logger.LogWarning("🔎 [Research] URL rejected by sandbox validation: {Url} — {Reason}",
                    url, validation.DenialReason);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🔎 [Research] URL validation failed for {Url}", url);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Headers-first so the body is streamed and the 512 KB cap below is
            // enforced before large payloads are buffered.
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            // AllowAutoRedirect=false ⇒ 3xx lands here and is skipped, so an
            // unvalidated redirect target is never followed.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("🔎 [Research] Skipping {Url}: HTTP {Status}",
                    url, (int)response.StatusCode);
                return null;
            }

            // ── 2. Content-type allow-list ──
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var isHtml = mediaType == "text/html";
            var isPlainText = mediaType == "text/plain";
            if (!isHtml && !isPlainText)
            {
                _logger.LogInformation("🔎 [Research] Skipping {Url}: unsupported content type '{MediaType}'",
                    url, mediaType ?? "(none)");
                return null;
            }

            // ── 3. Capped body read (truncates, never throws on oversize) ──
            var raw = await ReadAtMostAsync(response, ResearchLimits.MaxContentBytes, ct);
            var text = DecodeText(raw, response.Content.Headers.ContentType?.CharSet);
            if (string.IsNullOrWhiteSpace(text)) return null;

            // ── 4. Readable-text extraction ──
            var source = isHtml
                ? ExtractFromHtml(url, text)
                : new ResearchSource(url, DeriveTitleFromUrl(url), CollapseAndCap(text, ResearchLimits.MaxExtractedCharsPerSource));

            if (source is null || string.IsNullOrWhiteSpace(source.Content))
            {
                _logger.LogInformation("🔎 [Research] Skipping {Url}: no readable text extracted", url);
                return null;
            }

            return source;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes HttpRequestException, the client's own 15s timeout
            // (TaskCanceledException with ct NOT cancelled), and malformed HTML.
            _logger.LogWarning(ex, "🔎 [Research] Failed to fetch {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from the response body and
    /// stops silently at the cap — oversize bodies are truncated, not errors.
    /// </summary>
    private static async Task<byte[]> ReadAtMostAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[ReadBufferBytes];

        while (buffered.Length < maxBytes)
        {
            var toRead = (int)Math.Min(buffer.Length, maxBytes - buffered.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) break;
            buffered.Write(buffer, 0, read);
        }

        return buffered.ToArray();
    }

    private static string DecodeText(byte[] raw, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"', '\'')); }
            catch { /* unknown charset — fall back to UTF-8 */ }
        }

        // A truncated multi-byte sequence at the 512 KB cut must not throw.
        return encoding.GetString(raw);
    }

    private ResearchSource? ExtractFromHtml(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var strippable = doc.DocumentNode.SelectNodes(StrippedNodesXPath);
        if (strippable is not null)
        {
            foreach (var node in strippable.ToList())
                node.Remove();
        }

        var rawTitle = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        var title = CollapseAndCap(HtmlEntity.DeEntitize(rawTitle ?? string.Empty), MaxTitleChars);
        if (title.Length == 0) title = DeriveTitleFromUrl(url);

        var bodyNode = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var bodyText = CollapseAndCap(
            HtmlEntity.DeEntitize(bodyNode.InnerText ?? string.Empty),
            ResearchLimits.MaxExtractedCharsPerSource);

        return bodyText.Length == 0 ? null : new ResearchSource(url, title, bodyText);
    }

    private static string CollapseAndCap(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string collapsed;
        try
        {
            collapsed = WhitespaceRun.Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            collapsed = text.Trim();
        }

        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars];
    }

    private static string DeriveTitleFromUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
