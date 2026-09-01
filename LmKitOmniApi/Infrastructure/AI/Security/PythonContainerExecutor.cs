using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Container-backed <see cref="IPythonCodeExecutor"/>. Untrusted Python has full
/// system access, so — unlike the in-process Jint JavaScript sandbox
/// (<see cref="ExecutionSandboxEngine"/>) — isolation is enforced at the OS/container
/// boundary via a hardened <c>docker run</c>: no network, non-root (nobody),
/// all capabilities dropped, no-new-privileges, read-only rootfs with a small noexec
/// tmpfs, and memory / swap / cpu / pids / wall-clock limits.
///
/// Every failure mode (disabled, empty/over-limit script, timeout, non-zero exit,
/// launch failure) comes back as a bracketed, Vietnamese, agent-readable string —
/// mirroring the Jint engine's "[Sandbox Error] …" style — and the raw exception is
/// never leaked to the agent. Output is capped exactly like the Jint engine's
/// CapResult so a script can never flood the model context.
/// </summary>
public sealed class PythonContainerExecutor : IPythonCodeExecutor
{
    // ── Bracketed, agent-readable status/failure messages (Vietnamese) ──
    private const string NotConfiguredMessage =
        "[Code Interpreter] Trình thông dịch Python chưa được cấu hình.";
    private const string EmptyCodeMessage =
        "[Code Interpreter] Không có mã Python để thực thi.";
    private const string LaunchFailedMessage =
        "[Code Interpreter] Không khởi chạy được môi trường Python.";
    private const string NoOutputMessage = "(không có đầu ra)";
    private const string StdErrDivider = "--- stderr ---";

    // Wall-clock grace added on top of the configured budget so container spin-up /
    // tear-down overhead isn't charged against the script's compute time; the
    // ProcessRunner's kill is the backstop enforcing the limit.
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(5);

    // The container writes its source here; excluded from produced-file collection.
    private const string ScriptFileName = "main.py";

    private readonly CodeInterpreterOptions _options;
    private readonly IProcessRunner _runner;
    private readonly UserResourceAccessService _resources;
    private readonly ILogger<PythonContainerExecutor> _logger;

    public PythonContainerExecutor(
        IOptions<CodeInterpreterOptions> options,
        IProcessRunner runner,
        UserResourceAccessService resources,
        ILogger<PythonContainerExecutor> logger)
    {
        _options = options.Value;
        _runner = runner;
        _resources = resources;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Image);

    /// <inheritdoc />
    public async Task<PythonExecutionResult> ExecuteAsync(string code, Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return PythonExecutionResult.TextOnly(NotConfiguredMessage);

        if (string.IsNullOrWhiteSpace(code))
            return PythonExecutionResult.TextOnly(EmptyCodeMessage);

        if (code.Length > _options.MaxScriptChars)
            return PythonExecutionResult.TextOnly($"[Code Interpreter] Đoạn mã vượt quá giới hạn {_options.MaxScriptChars} ký tự.");

        // Per-run scratch dir under the system temp root. It is mounted rw into the
        // container as /work so the script can do file I/O during the run, and is
        // deleted (recursively, best-effort) in the finally below — it is ephemeral.
        var scratchDir = Path.Combine(Path.GetTempPath(), "lmkit-pyexec", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(scratchDir);

            // Default WriteAllTextAsync encoding is UTF-8 without BOM — exactly what
            // the Python runtime expects for a source file.
            var scriptPath = Path.Combine(scratchDir, ScriptFileName);
            await File.WriteAllTextAsync(scriptPath, code, ct);

            var arguments = BuildDockerArguments(scratchDir);
            var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) + TimeoutGrace;

            _logger.LogInformation(
                "🐍 [Code Interpreter] Chạy Python trong container (image {Image}, {Timeout}s)...",
                _options.Image, _options.TimeoutSeconds);

            var result = await _runner.RunAsync(_options.RuntimePath, arguments, stdin: null, timeout, ct);

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "⏱️ [Code Interpreter] Thực thi vượt quá {Timeout}s và đã bị dừng.",
                    _options.TimeoutSeconds);
                return PythonExecutionResult.TextOnly(
                    $"[Code Interpreter] Thực thi vượt quá {_options.TimeoutSeconds}s và đã bị dừng.");
            }

            if (result.ExitCode != 0)
            {
                // Surface the container's own diagnostics (capped) so the ReAct model
                // can self-correct. Prefer stderr; fall back to stdout when empty.
                var captured = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                _logger.LogWarning(
                    "❌ [Code Interpreter] Python thoát với mã {Exit}.", result.ExitCode);
                // A failed run's partial files are not returned.
                return PythonExecutionResult.TextOnly($"[Code Interpreter] Lỗi (exit {result.ExitCode}):\n{Cap(captured)}");
            }

            // Success: harvest any files the script wrote to /work (minus the source
            // file) BEFORE the finally deletes the scratch dir, persisting them under
            // the caller's isolated upload root for return to the user.
            var files = await CollectProducedFilesAsync(scratchDir, tenantId, userId, ct);

            _logger.LogInformation(
                "✅ [Code Interpreter] Python thực thi thành công ({FileCount} tệp kết quả).", files.Count);
            return new PythonExecutionResult(Cap(CombineOutput(result.StdOut, result.StdErr)), files);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation propagates unchanged.
            throw;
        }
        catch (Exception ex)
        {
            // Runtime missing, failed to spawn, scratch I/O error, anything else:
            // log for operators, but never leak raw details to the agent.
            _logger.LogWarning(ex, "❌ [Code Interpreter] Không khởi chạy được môi trường Python.");
            return PythonExecutionResult.TextOnly(LaunchFailedMessage);
        }
        finally
        {
            TryDeleteDirectory(scratchDir);
        }
    }

    /// <summary>
    /// Copies files the script produced under <paramref name="scratchDir"/> (the
    /// mounted /work, minus main.py) into the caller's isolated upload root, applying
    /// count / per-file / total-size caps. Returns descriptors keyed by a
    /// server-generated on-disk name (never the script-chosen name) so the download
    /// endpoint path is unguessable and traversal-safe. Best-effort: a single
    /// unreadable/oversized file is skipped, never fatal to the run.
    /// </summary>
    private async Task<IReadOnlyList<ProducedFile>> CollectProducedFilesAsync(
        string scratchDir, Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (_options.MaxOutputFiles <= 0)
            return Array.Empty<ProducedFile>();

        List<string> candidates;
        try
        {
            // Top-level only (scripts write to cwd=/work); deterministic order.
            candidates = Directory.EnumerateFiles(scratchDir, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), ScriptFileName, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🧹 [Code Interpreter] Không thể liệt kê tệp kết quả.");
            return Array.Empty<ProducedFile>();
        }

        if (candidates.Count == 0)
            return Array.Empty<ProducedFile>();

        var uploadDir = _resources.GetUploadDirectory(tenantId, userId);
        Directory.CreateDirectory(uploadDir);

        var produced = new List<ProducedFile>();
        long totalBytes = 0;

        foreach (var sourcePath in candidates)
        {
            if (produced.Count >= _options.MaxOutputFiles) break;

            long size;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists) continue;
                size = info.Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "🧹 [Code Interpreter] Bỏ qua tệp không đọc được.");
                continue;
            }

            if (size <= 0 || size > _options.MaxOutputFileBytes) continue;
            if (totalBytes + size > _options.MaxTotalOutputFileBytes) break;

            var originalName = Path.GetFileName(sourcePath);
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            var storedName = $"{Guid.NewGuid():N}{extension}";
            var destinationPath = Path.Combine(uploadDir, storedName);

            try
            {
                await using (var source = File.OpenRead(sourcePath))
                await using (var destination = File.Create(destinationPath))
                {
                    await source.CopyToAsync(destination, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "🧹 [Code Interpreter] Không thể lưu tệp kết quả {Name}.", originalName);
                continue;
            }

            totalBytes += size;
            produced.Add(new ProducedFile(storedName, originalName, ContentTypeFor(extension), size));
        }

        return produced;
    }

    /// <summary>Best-effort MIME lookup for a produced file, defaulting to a binary download.</summary>
    private static string ContentTypeFor(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".txt" or ".log" => "text/plain",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Builds the hardened <c>docker run</c> argument list. This is the security
    /// core; each flag is a separate list entry so nothing is shell-interpreted.
    ///  - <c>--network none</c>: no network at all (the key SSRF / exfiltration guard).
    ///  - <c>--user 65534:65534</c> (nobody:nogroup), <c>--cap-drop ALL</c>,
    ///    <c>--security-opt no-new-privileges</c>: run de-privileged, no escalation.
    ///  - <c>--read-only</c> rootfs + <c>--tmpfs /tmp:…,noexec</c>: nothing writable
    ///    and executable except the mounted /work scratch.
    ///  - memory == memory-swap: swap disabled; <c>--cpus</c> / <c>--pids-limit</c>:
    ///    CPU + fork-bomb bounds.
    /// The scratch is mounted rw so the script can write files under /work during the
    /// run; files produced there are harvested (see CollectProducedFilesAsync) into the
    /// caller's upload root for return to the user, then the scratch is deleted.
    /// </summary>
    private List<string> BuildDockerArguments(string scratchDir)
    {
        var memory = $"{_options.MemoryMb}m";
        var cpus = _options.Cpus.ToString(CultureInfo.InvariantCulture);

        return new List<string>
        {
            "run",
            "--rm",
            "--network", "none",
            "--interactive=false",
            "--memory", memory,
            "--memory-swap", memory,          // == memory ⇒ swap disabled
            "--cpus", cpus,
            "--pids-limit", "128",
            "--user", "65534:65534",          // nobody:nogroup
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--read-only",
            "--tmpfs", "/tmp:rw,size=64m,noexec",
            "--workdir", "/work",
            "--volume", $"{scratchDir}:/work:rw",
            _options.Image,
            "python", "/work/main.py",
        };
    }

    /// <summary>
    /// Combines captured streams for a successful run: stdout, then stderr under a
    /// divider when non-empty. Empty on both sides yields the explicit no-output notice.
    /// </summary>
    private static string CombineOutput(string stdOut, string stdErr)
    {
        var hasOut = !string.IsNullOrEmpty(stdOut);
        var hasErr = !string.IsNullOrEmpty(stdErr);

        if (!hasOut && !hasErr)
            return NoOutputMessage;

        if (!hasErr)
            return stdOut;

        var errBlock = $"{StdErrDivider}\n{stdErr}";
        return hasOut ? $"{stdOut}\n{errBlock}" : errBlock;
    }

    /// <summary>
    /// Caps combined output at MaxOutputChars with an explicit truncation marker —
    /// mirrors <see cref="ExecutionSandboxEngine"/>'s CapResult so a script can never
    /// flood the agent context.
    /// </summary>
    private string Cap(string text)
    {
        var max = _options.MaxOutputChars;
        return text.Length <= max
            ? text
            : text[..max] + $"\n[Kết quả đã bị cắt bớt vì vượt quá {max} ký tự]";
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort; a leftover temp dir must never fail the run.
            _logger.LogDebug(ex, "🧹 [Code Interpreter] Không thể dọn thư mục tạm {Dir}.", directory);
        }
    }
}
