using System.Globalization;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Container-backed <see cref="IComputerUseExecutor"/>. Each step is one hardened
/// <c>docker run</c> through the injectable <see cref="IProcessRunner"/> seam — the same
/// isolation core as <see cref="BrowserFetchExecutor"/> / <c>PythonContainerExecutor</c>:
/// non-root (nobody), <c>--cap-drop ALL</c>, <c>--security-opt no-new-privileges</c>,
/// read-only rootfs with a small noexec tmpfs, and memory / swap / cpu / pids /
/// wall-clock limits. The persistent browser profile lives in a per-session directory
/// mounted at <c>/session</c>, so the profile (cookies, page, scroll position) carries
/// across the steps of one run while the container itself is ephemeral (<c>--rm</c>).
///
/// The browser is DELIBERATELY networked (no <c>--network none</c>). Because that is a
/// wide SSRF surface, every <c>navigate</c> is validated by
/// <c>ToolSandboxService.ValidateUrlAsync</c> AND checked against the EXPLICIT
/// <see cref="ComputerUseOptions.AllowedHosts"/> allowlist (empty = deny all) BEFORE any
/// container launches. The allowlist is additionally propagated into the container via
/// the <c>COMPUTER_USE_ALLOWED_HOSTS</c> env var so a cooperating image can self-restrict,
/// and — when configured — the container is pinned to an operator egress-restricted
/// network (<c>--network &lt;name&gt;</c>). As with the read-only browse tool, the host
/// guards constrain only the initial navigation target; in-page redirects/subresources
/// are the operator network's responsibility.
///
/// Execution is LIVE-ONLY (it needs a real interactive-browser image); every failure
/// mode comes back as an observation with <see cref="ComputerUseObservation.Error"/> set,
/// never an exception, and the raw error is never leaked to the agent.
/// </summary>
public sealed class ComputerUseExecutor : IComputerUseExecutor
{
    // Wall-clock grace on top of the configured per-step budget so container spin-up /
    // tear-down isn't charged against the step; the ProcessRunner kill is the backstop.
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(5);

    private const int MaxUrlChars = 2048;
    private const string SessionMountTarget = "/session";
    private const string AllowedHostsEnvName = "COMPUTER_USE_ALLOWED_HOSTS";

    private readonly ComputerUseOptions _options;
    private readonly IProcessRunner _runner;
    private readonly ToolSandboxService _sandbox;
    private readonly UserResourceAccessService _resources;
    private readonly ILogger<ComputerUseExecutor> _logger;

    public ComputerUseExecutor(
        IOptions<ComputerUseOptions> options,
        IProcessRunner runner,
        ToolSandboxService sandbox,
        UserResourceAccessService resources,
        ILogger<ComputerUseExecutor> logger)
    {
        _options = options.Value;
        _runner = runner;
        _sandbox = sandbox;
        _resources = resources;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Image);

    /// <inheritdoc />
    public async Task<ComputerUseObservation> StepAsync(
        ComputerUseAction action,
        Guid tenantId,
        Guid userId,
        string sessionDirectory,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return ComputerUseObservation.Failed("[ComputerUse] Công cụ điều khiển trình duyệt chưa được cấu hình.");

        if (string.IsNullOrWhiteSpace(sessionDirectory))
            return ComputerUseObservation.Failed("[ComputerUse] Thiếu thư mục phiên làm việc.");

        // ── SSRF + allowlist gate — MUST run BEFORE any container launches (navigate only) ──
        if (action.Type == ComputerUseActionType.Navigate)
        {
            var url = action.Url?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url))
                return ComputerUseObservation.Failed("[ComputerUse] Không có URL để truy cập.");
            if (url.Length > MaxUrlChars)
                return ComputerUseObservation.Failed($"[ComputerUse] URL vượt quá giới hạn {MaxUrlChars} ký tự.");

            var validation = await _sandbox.ValidateUrlAsync(url, ct);
            if (!validation.IsAllowed)
            {
                _logger.LogWarning("🔒 [ComputerUse] URL bị từ chối bởi cổng SSRF: {Reason}", validation.DenialReason);
                return ComputerUseObservation.Failed($"[ComputerUse] URL bị từ chối: {validation.DenialReason}");
            }

            if (!IsHostAllowed(url, out var host))
            {
                _logger.LogWarning("🔒 [ComputerUse] Máy chủ '{Host}' không nằm trong danh sách cho phép.", host);
                return ComputerUseObservation.Failed(
                    $"[ComputerUse] Máy chủ '{host}' không nằm trong danh sách điều hướng được phép.");
            }
        }

        try
        {
            Directory.CreateDirectory(sessionDirectory);
            var arguments = BuildDockerArguments(sessionDirectory, action);
            var timeout = TimeSpan.FromSeconds(_options.StepTimeoutSeconds) + TimeoutGrace;

            _logger.LogInformation(
                "🖥️ [ComputerUse] Thực thi hành động '{Action}' trong container (image {Image}, {Timeout}s)...",
                action.Type, _options.Image, _options.StepTimeoutSeconds);

            var result = await _runner.RunAsync(_options.RuntimePath, arguments, stdin: null, timeout, ct);

            if (result.TimedOut)
            {
                _logger.LogWarning("⏱️ [ComputerUse] Bước vượt quá {Timeout}s và đã bị dừng.", _options.StepTimeoutSeconds);
                return ComputerUseObservation.Failed(
                    $"[ComputerUse] Bước vượt quá {_options.StepTimeoutSeconds}s và đã bị dừng.");
            }

            if (result.ExitCode != 0)
            {
                var captured = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                _logger.LogWarning("❌ [ComputerUse] Trình duyệt thoát với mã {Exit}.", result.ExitCode);
                return ComputerUseObservation.Failed($"[ComputerUse] Lỗi (exit {result.ExitCode}): {Truncate(captured, 500)}");
            }

            return await ParseObservationAsync(result.StdOut, sessionDirectory, tenantId, userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ [ComputerUse] Không thực thi được bước.");
            return ComputerUseObservation.Failed("[ComputerUse] Không khởi chạy được trình duyệt.");
        }
    }

    /// <summary>
    /// Exact, case-insensitive host allowlist. UNLIKE the read-only browse tool, an EMPTY
    /// allowlist means DENY ALL — the strict default for this high-power tool. Internal /
    /// loopback / metadata targets are already blocked by the SSRF gate above.
    /// </summary>
    private bool IsHostAllowed(string url, out string host)
    {
        var parsedHost = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        host = parsedHost;
        if (_options.AllowedHosts.Count == 0) return false; // deny-all default
        return _options.AllowedHosts.Any(allowed =>
            string.Equals(allowed, parsedHost, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the hardened <c>docker run</c> argument list. Each flag is a separate list
    /// entry so nothing is shell-interpreted, and the action JSON is the trailing
    /// positional argument passed to the image entrypoint (never concatenated into a
    /// shell string).
    ///  - NO <c>--network none</c>: the browser DELIBERATELY has egress; when
    ///    <see cref="ComputerUseOptions.NetworkName"/> is set the container is pinned to
    ///    that operator egress-restricted network.
    ///  - The navigation allowlist is propagated as an env var so a cooperating image can
    ///    self-restrict; the authoritative pre-navigation check already happened above.
    ///  - <c>--user 65534:65534</c>, <c>--cap-drop ALL</c>, <c>--security-opt
    ///    no-new-privileges</c>, <c>--read-only</c> + noexec tmpfs, memory==memory-swap
    ///    (swap off), <c>--cpus</c>, <c>--pids-limit</c>.
    ///  - The per-session profile dir is mounted rw at <c>/session</c>.
    /// </summary>
    private List<string> BuildDockerArguments(string sessionDirectory, ComputerUseAction action)
    {
        var memory = $"{_options.MemoryMb}m";
        var cpus = _options.Cpus.ToString(CultureInfo.InvariantCulture);
        var allowedHostsCsv = string.Join(",", _options.AllowedHosts);

        var args = new List<string>
        {
            "run",
            "--rm",
            "--interactive=false",
            "--memory", memory,
            "--memory-swap", memory,          // == memory ⇒ swap disabled
            "--cpus", cpus,
            "--pids-limit", _options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--user", "65534:65534",          // nobody:nogroup
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--read-only",
            "--tmpfs", "/tmp:rw,size=256m,noexec",
        };

        // Egress control: pin to an operator-restricted network when configured, and
        // always hand the cooperating image the navigation allowlist.
        if (!string.IsNullOrWhiteSpace(_options.NetworkName))
        {
            args.Add("--network");
            args.Add(_options.NetworkName);
        }
        args.Add("--env");
        args.Add($"{AllowedHostsEnvName}={allowedHostsCsv}");

        args.Add("--workdir");
        args.Add(SessionMountTarget);
        args.Add("--volume");
        args.Add($"{sessionDirectory}:{SessionMountTarget}:rw");

        args.Add(_options.Image);
        args.Add("--action");
        args.Add(SerializeAction(action));

        return args;
    }

    /// <summary>Serializes the action to the compact wire JSON the container entrypoint reads.</summary>
    private static string SerializeAction(ComputerUseAction action)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action.Type.ToString().ToLowerInvariant() };
        switch (action.Type)
        {
            case ComputerUseActionType.Navigate: payload["url"] = action.Url; break;
            case ComputerUseActionType.Click:
                if (action.Ref is int cr) payload["ref"] = cr;
                if (action.X is int cx) payload["x"] = cx;
                if (action.Y is int cy) payload["y"] = cy;
                break;
            case ComputerUseActionType.Type:
                if (action.Ref is int tr) payload["ref"] = tr;
                if (action.X is int tx) payload["x"] = tx;
                if (action.Y is int ty) payload["y"] = ty;
                payload["text"] = action.Text;
                break;
            case ComputerUseActionType.Key: payload["keys"] = action.Keys; break;
            case ComputerUseActionType.Scroll:
                payload["direction"] = action.Direction;
                payload["amount"] = action.Amount;
                break;
            case ComputerUseActionType.Wait: payload["ms"] = action.Ms; break;
        }
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Parses the container's single-line observation JSON, caps the element list, and
    /// harvests any screenshot the container wrote under the session dir into the caller's
    /// isolated upload root (server-generated name, byte-capped). Best-effort throughout:
    /// unparseable stdout ⇒ an error observation; a missing/oversized screenshot ⇒ a null
    /// file id but the rest of the observation is still returned.
    /// </summary>
    private async Task<ComputerUseObservation> ParseObservationAsync(
        string stdout, string sessionDirectory, Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return ComputerUseObservation.Failed("[ComputerUse] Không nhận được quan sát từ trình duyệt.");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ComputerUseObservation.Failed("[ComputerUse] Quan sát trả về không hợp lệ.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return ComputerUseObservation.Failed("[ComputerUse] Quan sát trả về không hợp lệ.");

        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;
        var url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

        var elements = new List<InteractiveElement>();
        if (root.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in els.EnumerateArray())
            {
                if (elements.Count >= _options.MaxElements) break;
                if (el.ValueKind != JsonValueKind.Object) continue;

                var refId = el.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var ri)
                    ? ri
                    : elements.Count + 1;
                var role = el.TryGetProperty("role", out var ro) && ro.ValueKind == JsonValueKind.String ? ro.GetString() ?? "" : "";
                var name = el.TryGetProperty("name", out var na) && na.ValueKind == JsonValueKind.String ? na.GetString() ?? "" : "";
                var value = el.TryGetProperty("value", out var va) && va.ValueKind == JsonValueKind.String ? va.GetString() : null;

                elements.Add(new InteractiveElement(
                    refId, Truncate(role, 40), Truncate(name, 200), value is null ? null : Truncate(value, 200)));
            }
        }

        string? screenshotId = null;
        if (root.TryGetProperty("screenshot", out var s) && s.ValueKind == JsonValueKind.String)
            screenshotId = await HarvestScreenshotAsync(s.GetString(), sessionDirectory, tenantId, userId, ct);

        return new ComputerUseObservation
        {
            ScreenshotFileId = screenshotId,
            Elements = elements,
            Url = url,
            Title = title,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
        };
    }

    /// <summary>
    /// Copies a screenshot the container wrote under the session dir into the caller's
    /// isolated upload root under an unguessable server-generated name, enforcing the
    /// per-screenshot byte cap. Returns the stored id, or null if absent/oversized/unreadable.
    /// </summary>
    private async Task<string?> HarvestScreenshotAsync(
        string? screenshotName, string sessionDirectory, Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(screenshotName)) return null;

        // Never trust the container-reported name as a path: collapse to a bare file name
        // and resolve strictly inside the session dir.
        var safeName = Path.GetFileName(screenshotName);
        if (string.IsNullOrWhiteSpace(safeName)) return null;

        var sourcePath = Path.Combine(sessionDirectory, safeName);
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length <= 0) return null;
            if (info.Length > _options.MaxScreenshotBytes)
            {
                _logger.LogWarning("🧹 [ComputerUse] Ảnh chụp màn hình {Bytes}B vượt giới hạn — bỏ qua.", info.Length);
                return null;
            }

            var uploadDir = _resources.GetUploadDirectory(tenantId, userId);
            Directory.CreateDirectory(uploadDir);

            var extension = Path.GetExtension(safeName);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var destinationPath = Path.Combine(uploadDir, storedName);

            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = File.Create(destinationPath))
            {
                await source.CopyToAsync(destination, ct);
            }

            return storedName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🧹 [ComputerUse] Không thể lưu ảnh chụp màn hình.");
            return null;
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
