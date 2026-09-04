using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Default <see cref="IWebReadService"/>. Pure policy/plumbing layer — the actual
/// LM-Kit fetch lives behind the <see cref="IWebPageReader"/> seam
/// (<see cref="LmKitWebPageReader"/>), so everything this class does (enable gating,
/// SSRF pre-flight, URL parsing, length cap, citation) is hermetically CI-testable with
/// a fake reader and no network.
///
/// Two independent egress guards defend the fetch:
///  1. a pre-flight <see cref="ToolSandboxService.ValidateUrlAsync"/> — vets the host
///     AND every DNS-resolved IP against private/loopback/link-local/metadata ranges,
///     and runs BEFORE the reader is ever touched; and
///  2. the LM-Kit <c>WebEgressPolicy</c> the reader builds — public-web only, DNS-pinned,
///     re-validated on every redirect hop (see <see cref="LmKitWebPageReader"/>).
/// </summary>
public sealed class LmKitWebReadService : IWebReadService
{
    // Bracketed, agent-readable status/failure messages (Vietnamese, mirroring the
    // Python/browser executors). These are untrusted tool output downstream.
    private const string NotEnabledMessage = "[WebRead] Công cụ đọc web chưa được bật.";
    private const string EmptyUrlMessage = "[WebRead] Không có URL để đọc.";
    private const string UrlTooLongMessage = "[WebRead] URL quá dài.";
    private const string FetchFailedMessage =
        "[WebRead] Không đọc được trang (lỗi tải hoặc nội dung không hợp lệ).";
    private const string NoContentMessage = "(không có nội dung)";

    // Defensive upper bound on URL length (scheme is restricted to http/https by the gate).
    private const int MaxUrlChars = 2048;

    private readonly WebReadOptions _options;
    private readonly ToolSandboxService _sandbox;
    private readonly IWebPageReader _reader;
    private readonly ILogger<LmKitWebReadService> _logger;

    public LmKitWebReadService(
        IOptions<WebReadOptions> options,
        ToolSandboxService sandbox,
        IWebPageReader reader,
        ILogger<LmKitWebReadService> logger)
    {
        _options = options.Value;
        _sandbox = sandbox;
        _reader = reader;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<string> ReadAsync(string query, CancellationToken ct = default)
    {
        // Enable gate: when off, never fetch (mirrors run_python / browse_web).
        if (!IsEnabled)
            return NotEnabledMessage;

        if (string.IsNullOrWhiteSpace(query))
            return EmptyUrlMessage;

        // Accept "url|what-to-extract": only the URL is fetched; the trailing
        // instruction is context for the agent's own reasoning, never executed and
        // never sent to the fetcher (mirrors the browse_web contract).
        var separator = query.IndexOf('|');
        var url = (separator >= 0 ? query[..separator] : query).Trim();

        if (string.IsNullOrWhiteSpace(url))
            return EmptyUrlMessage;
        if (url.Length > MaxUrlChars)
            return UrlTooLongMessage;

        // Pre-flight SSRF gate (defense-in-depth) — runs BEFORE the reader is touched,
        // so an internal/loopback/metadata target is refused before any fetch or DNS
        // egress. The LM-Kit WebEgressPolicy inside the reader is the second gate.
        var gate = await _sandbox.ValidateUrlAsync(url, ct);
        if (!gate.IsAllowed)
        {
            _logger.LogWarning("🌐 [WebRead] URL refused by SSRF gate: {Reason}", gate.DenialReason);
            return $"[WebRead] URL bị từ chối: {gate.DenialReason}";
        }

        string extracted;
        try
        {
            extracted = await _reader.ReadAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never leak the raw exception (which may carry the egress gate's own
            // refusal reason for a redirect hop) to the agent as anything but a safe,
            // bracketed message.
            _logger.LogWarning(ex, "🌐 [WebRead] Fetch/read failed for {Url}", url);
            return FetchFailedMessage;
        }

        extracted = (extracted ?? string.Empty).Trim();
        if (extracted.Length == 0)
            return NoContentMessage;

        // Length cap (defense-in-depth on top of WebReadTool.Options.MaxContentChars):
        // the content lands in the model context, so a page can never flood it.
        var capped = Cap(extracted, _options.MaxContentChars);

        // Prepend an explicit source line so the agent can cite the page it read.
        return $"Nguồn: {url}\n\n{capped}";
    }

    private static string Cap(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
            return text;
        return text[..maxChars] + $"\n[Nội dung đã bị cắt bớt vì vượt quá {maxChars} ký tự]";
    }
}
