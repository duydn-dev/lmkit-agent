using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Container-backed <see cref="IBrowserFetchExecutor"/>. A headless browser has full
/// system + network access, so — like <see cref="PythonContainerExecutor"/> — isolation
/// is enforced at the OS/container boundary via a hardened <c>docker run</c>: non-root
/// (nobody), all capabilities dropped, no-new-privileges, read-only rootfs with a small
/// writable tmpfs for the browser profile, and memory / swap / cpu / pids / wall-clock
/// limits, all through the injectable <see cref="IProcessRunner"/> seam.
///
/// IMPORTANT DIFFERENCE from the Python sandbox: browsing needs the network, so the
/// container is NOT launched with <c>--network none</c>. That makes a networked browser
/// a WIDER SSRF surface than the no-network interpreter, so:
///  1. <see cref="ToolSandboxService.ValidateUrlAsync"/> is called BEFORE any browser is
///     launched and refuses internal/loopback/link-local/metadata targets (host AND every
///     DNS-resolved IP are vetted, closing the DNS-rebinding window at validation time); and
///  2. an optional operator <see cref="BrowserFetchOptions.AllowedHosts"/> allowlist can
///     further restrict permitted destination hosts.
/// Both guards constrain only the INITIAL navigation target — redirects and subresources
/// the page loads inside the container are NOT re-vetted by the host, so operators should
/// additionally place the browser container on an egress-restricted network. This is
/// documented as the accepted v1 caveat.
///
/// Every failure mode (disabled, empty/over-limit URL, SSRF/allowlist refusal, timeout,
/// non-zero exit, launch failure) comes back as a bracketed, Vietnamese, agent-readable
/// string — mirroring the Python executor — and the raw exception is never leaked to the
/// agent. Rendered output is capped exactly like the Python executor's Cap so a page can
/// never flood the model context.
///
/// v1 is TEXT-ONLY: it returns the rendered page text and always leaves
/// <see cref="BrowserFetchResult.ScreenshotFileId"/> null. Persisting a screenshot as a
/// <see cref="ProducedFile"/> (harvested from a mounted /work exactly like
/// <c>PythonContainerExecutor.CollectProducedFilesAsync</c>, then surfaced as a [FILE:]
/// marker via the orchestrator's file sink) is a straightforward future extension; it is
/// deferred because execution is live-only (needs a real browser container) and cannot be
/// CI-verified without one.
/// </summary>
public sealed class BrowserFetchExecutor : IBrowserFetchExecutor
{
    // ── Bracketed, agent-readable status/failure messages (Vietnamese) ──
    private const string NotConfiguredMessage =
        "[Browse] Công cụ duyệt web chưa được cấu hình.";
    private const string EmptyUrlMessage =
        "[Browse] Không có URL để truy cập.";
    private const string LaunchFailedMessage =
        "[Browse] Không khởi chạy được trình duyệt.";
    private const string NoOutputMessage = "(không có nội dung)";

    // Wall-clock grace added on top of the configured budget so container spin-up /
    // tear-down overhead isn't charged against the page's render time; the
    // ProcessRunner's kill is the backstop enforcing the limit.
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(5);

    // Defensive upper bound on the URL length so a pathological value can never bloat
    // the argument list (the scheme is already restricted to http/https by the SSRF gate).
    private const int MaxUrlChars = 2048;

    private readonly BrowserFetchOptions _options;
    private readonly IProcessRunner _runner;
    private readonly ToolSandboxService _sandbox;
    private readonly ILogger<BrowserFetchExecutor> _logger;

    public BrowserFetchExecutor(
        IOptions<BrowserFetchOptions> options,
        IProcessRunner runner,
        ToolSandboxService sandbox,
        ILogger<BrowserFetchExecutor> logger)
    {
        _options = options.Value;
        _runner = runner;
        _sandbox = sandbox;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Image);

    /// <inheritdoc />
    public async Task<BrowserFetchResult> FetchAsync(string url, Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return BrowserFetchResult.TextOnly(NotConfiguredMessage);

        if (string.IsNullOrWhiteSpace(url))
            return BrowserFetchResult.TextOnly(EmptyUrlMessage);

        url = url.Trim();

        if (url.Length > MaxUrlChars)
            return BrowserFetchResult.TextOnly($"[Browse] URL vượt quá giới hạn {MaxUrlChars} ký tự.");

        // ── SSRF gate — MUST run BEFORE any browser is launched ──
        // This container is networked, so the URL guard is the primary defense. It
        // refuses non-http(s) schemes and any host/IP that resolves into a
        // private/loopback/link-local/metadata range.
        var validation = await _sandbox.ValidateUrlAsync(url, ct);
        if (!validation.IsAllowed)
        {
            _logger.LogWarning("🔒 [Browse] URL bị từ chối bởi cổng SSRF: {Reason}", validation.DenialReason);
            return BrowserFetchResult.TextOnly($"[Browse] URL bị từ chối: {validation.DenialReason}");
        }

        // ── Optional operator egress allowlist — also BEFORE any launch ──
        if (!IsHostAllowed(url, out var host))
        {
            _logger.LogWarning("🔒 [Browse] Máy chủ '{Host}' không nằm trong danh sách cho phép.", host);
            return BrowserFetchResult.TextOnly($"[Browse] Máy chủ '{host}' không nằm trong danh sách cho phép.");
        }

        try
        {
            var arguments = BuildDockerArguments(url);
            var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) + TimeoutGrace;

            _logger.LogInformation(
                "🌐 [Browse] Mở trang trong container (image {Image}, host {Host}, {Timeout}s)...",
                _options.Image, host, _options.TimeoutSeconds);

            var result = await _runner.RunAsync(_options.RuntimePath, arguments, stdin: null, timeout, ct);

            if (result.TimedOut)
            {
                _logger.LogWarning("⏱️ [Browse] Truy cập vượt quá {Timeout}s và đã bị dừng.", _options.TimeoutSeconds);
                return BrowserFetchResult.TextOnly(
                    $"[Browse] Truy cập vượt quá {_options.TimeoutSeconds}s và đã bị dừng.");
            }

            if (result.ExitCode != 0)
            {
                // Surface the container's own diagnostics (capped) so the ReAct model can
                // self-correct. Prefer stderr; fall back to stdout when empty.
                var captured = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                _logger.LogWarning("❌ [Browse] Trình duyệt thoát với mã {Exit}.", result.ExitCode);
                return BrowserFetchResult.TextOnly($"[Browse] Lỗi (exit {result.ExitCode}):\n{Cap(captured)}");
            }

            // Success: the rendered page text is on stdout. Browser stderr is noisy
            // (font/GPU warnings), so it is intentionally not merged into the observation.
            var rendered = result.StdOut;
            _logger.LogInformation("✅ [Browse] Tải trang thành công ({Chars} ký tự).", rendered.Length);
            return BrowserFetchResult.TextOnly(string.IsNullOrEmpty(rendered) ? NoOutputMessage : Cap(rendered));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation propagates unchanged.
            throw;
        }
        catch (Exception ex)
        {
            // Runtime missing, failed to spawn, anything else: log for operators, but
            // never leak raw details to the agent.
            _logger.LogWarning(ex, "❌ [Browse] Không khởi chạy được trình duyệt.");
            return BrowserFetchResult.TextOnly(LaunchFailedMessage);
        }
    }

    /// <summary>
    /// True when the URL's host is permitted by the optional operator allowlist. An empty
    /// allowlist permits any host (the SSRF gate already blocked internal targets). Exact,
    /// case-insensitive host match — mirrors <c>DbEgressValidator</c>. <paramref name="host"/>
    /// is the parsed host for logging/messaging.
    /// </summary>
    private bool IsHostAllowed(string url, out string host)
    {
        var parsedHost = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        host = parsedHost;

        if (_options.AllowedHosts.Count == 0)
            return true;

        return _options.AllowedHosts.Any(allowed =>
            string.Equals(allowed, parsedHost, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the hardened <c>docker run</c> argument list. This is the security core;
    /// each flag is a separate list entry so nothing is shell-interpreted, and the
    /// validated http(s) URL is the trailing positional argument passed to the image's
    /// entrypoint (never concatenated into a shell string).
    ///  - NO <c>--network none</c>: the browser DELIBERATELY has network egress (see class
    ///    remarks) — this is the one hardening flag the Python sandbox has that this tool
    ///    cannot, which is exactly why the SSRF gate above is mandatory.
    ///  - <c>--user 65534:65534</c> (nobody:nogroup), <c>--cap-drop ALL</c>,
    ///    <c>--security-opt no-new-privileges</c>: run de-privileged, no escalation.
    ///  - <c>--read-only</c> rootfs + <c>--tmpfs /tmp:…,noexec</c>: nothing writable and
    ///    executable except an ephemeral tmpfs for the browser profile/cache.
    ///  - memory == memory-swap: swap disabled; <c>--cpus</c> / <c>--pids-limit</c>:
    ///    CPU + fork-bomb bounds (a browser spawns more helper processes than an
    ///    interpreter, so the pids ceiling is higher than the Python sandbox's).
    /// </summary>
    private List<string> BuildDockerArguments(string url)
    {
        var memory = $"{_options.MemoryMb}m";
        var cpus = _options.Cpus.ToString(CultureInfo.InvariantCulture);

        return new List<string>
        {
            "run",
            "--rm",
            "--interactive=false",
            "--memory", memory,
            "--memory-swap", memory,          // == memory ⇒ swap disabled
            "--cpus", cpus,
            "--pids-limit", "512",            // browsers fork more helpers than an interpreter
            "--user", "65534:65534",          // nobody:nogroup
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--read-only",
            "--tmpfs", "/tmp:rw,size=256m,noexec",
            "--workdir", "/tmp",
            _options.Image,
            url,                               // validated http(s) URL → the image entrypoint
        };
    }

    /// <summary>
    /// Caps rendered output at MaxOutputChars with an explicit truncation marker —
    /// mirrors <see cref="PythonContainerExecutor"/>'s Cap so a page can never flood the
    /// agent context.
    /// </summary>
    private string Cap(string text)
    {
        var max = _options.MaxOutputChars;
        return text.Length <= max
            ? text
            : text[..max] + $"\n[Nội dung đã bị cắt bớt vì vượt quá {max} ký tự]";
    }
}
