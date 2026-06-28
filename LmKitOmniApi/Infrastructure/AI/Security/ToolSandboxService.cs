using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Tool Sandbox Service — wraps tool execution in an isolated environment with:
/// 1. File System Restriction: tools can only access whitelisted directories
/// 2. Output Size Limit: prevents context overflow from tool output
/// 3. Execution Timeout: kills long-running tools
/// 4. Resource Budget: tracks cumulative resource usage per request
/// 5. Path Traversal Protection: blocks ../ and absolute path escapes
/// 6. Sensitive Path Blocking: prevents access to system/config directories
/// 
/// Addresses OWASP LLM09: Improper Output Handling + Tool Misuse.
/// Inspired by console_net/ai-agents/sandboxing.
/// </summary>
public class ToolSandboxService
{
    private readonly ILogger<ToolSandboxService> _logger;

    // ── File System Sandbox ──
    private readonly HashSet<string> _allowedBasePaths;
    private static readonly string[] DefaultAllowedPaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "Uploads"),
        Path.Combine(Directory.GetCurrentDirectory(), "Documents"),
        Path.Combine(Directory.GetCurrentDirectory(), "Temp"),
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
    };

    // ── Blocked Paths (never accessible, even if under allowed base) ──
    private static readonly string[] BlockedPathPatterns = new[]
    {
        "appsettings", ".env", "secrets", "credentials",
        "web.config", "launchSettings", "Program.cs",
        ".git", ".ssh", ".aws", "node_modules",
        "shadow", "passwd", "etc/hosts"
    };

    // ── Output Limits ──
    private const int MaxToolOutputChars = 5000;
    private const int MaxToolOutputLinesPerResult = 200;

    // ── Execution Limits ──
    private static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(30);
    private const int MaxToolCallsPerRequest = 15;

    // ── Resource Tracking ──
    private int _currentToolCallCount = 0;
    private readonly object _countLock = new();

    public ToolSandboxService(ILogger<ToolSandboxService> logger)
    {
        _logger = logger;
        _allowedBasePaths = new HashSet<string>(
            DefaultAllowedPaths.Select(p => Path.GetFullPath(p)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Execute a tool function within the sandbox.
    /// Enforces timeout, output limits, and resource budget.
    /// </summary>
    public async Task<SandboxResult> ExecuteInSandboxAsync(
        string toolName,
        Func<CancellationToken, Task<string>> toolAction,
        CancellationToken ct = default)
    {
        // Check resource budget
        int callNumber;
        lock (_countLock)
        {
            _currentToolCallCount++;
            callNumber = _currentToolCallCount;
        }

        if (callNumber > MaxToolCallsPerRequest)
        {
            _logger.LogWarning("🔒 [Sandbox] Tool budget exceeded: {Count}/{Max}. Tool '{Tool}' blocked.",
                callNumber, MaxToolCallsPerRequest, toolName);
            return SandboxResult.Blocked(toolName,
                $"Đã vượt quá giới hạn {MaxToolCallsPerRequest} lần gọi công cụ trong một yêu cầu.");
        }

        try
        {
            // Execute with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DefaultToolTimeout);

            _logger.LogInformation("🔒 [Sandbox] Executing tool '{Tool}' (call #{Count})", toolName, callNumber);

            var rawOutput = await toolAction(cts.Token);

            // Sanitize and limit output
            var sanitized = SanitizeToolOutput(rawOutput, toolName);

            return SandboxResult.Success(toolName, sanitized);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("🔒 [Sandbox] Tool '{Tool}' TIMED OUT after {Timeout}s",
                toolName, DefaultToolTimeout.TotalSeconds);
            return SandboxResult.TimedOut(toolName, DefaultToolTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔒 [Sandbox] Tool '{Tool}' FAILED in sandbox", toolName);
            // Don't leak internal error details to the AI
            return SandboxResult.Error(toolName, SanitizeErrorMessage(ex));
        }
    }

    /// <summary>
    /// Validate a file path before allowing tool access.
    /// Blocks path traversal, absolute path escapes, and sensitive files.
    /// </summary>
    public PathValidationResult ValidateFilePath(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return PathValidationResult.Deny("Đường dẫn file trống.");

        // Normalize the path
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception)
        {
            return PathValidationResult.Deny("Đường dẫn file không hợp lệ.");
        }

        // Check for path traversal attempts
        if (requestedPath.Contains("..") || requestedPath.Contains("~"))
        {
            _logger.LogWarning("🔒 [Sandbox] Path traversal attempt blocked: '{Path}'", requestedPath);
            return PathValidationResult.Deny("Không được phép sử dụng đường dẫn tương đối (..) hoặc (~).");
        }

        // Check against blocked patterns
        var lowerPath = fullPath.ToLowerInvariant().Replace("\\", "/");
        foreach (var blocked in BlockedPathPatterns)
        {
            if (lowerPath.Contains(blocked.ToLowerInvariant()))
            {
                _logger.LogWarning("🔒 [Sandbox] Blocked path pattern detected: '{Pattern}' in '{Path}'",
                    blocked, requestedPath);
                return PathValidationResult.Deny($"Truy cập bị từ chối: file '{Path.GetFileName(requestedPath)}' nằm trong vùng bị chặn.");
            }
        }

        // Check if under allowed base paths
        var isAllowed = _allowedBasePaths.Any(basePath =>
            fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            _logger.LogWarning("🔒 [Sandbox] File access denied — outside sandbox: '{Path}'", fullPath);
            return PathValidationResult.Deny(
                $"File nằm ngoài vùng được phép. Chỉ cho phép truy cập: {string.Join(", ", _allowedBasePaths.Select(Path.GetFileName))}");
        }

        return PathValidationResult.Allow(fullPath);
    }

    /// <summary>
    /// Sanitize a URL before allowing web requests.
    /// Blocks internal network, localhost, and private IPs.
    /// </summary>
    public PathValidationResult ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return PathValidationResult.Deny("URL trống.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return PathValidationResult.Deny("URL không hợp lệ.");

        // Block internal network (SSRF protection)
        var host = uri.Host.ToLowerInvariant();
        var blockedHosts = new[] { "localhost", "127.0.0.1", "0.0.0.0", "::1", "169.254.", "10.", "172.16.", "192.168.", "metadata.google" };
        
        if (blockedHosts.Any(b => host.StartsWith(b) || host.Contains(b)))
        {
            _logger.LogWarning("🔒 [Sandbox] SSRF attempt blocked: '{Url}'", url);
            return PathValidationResult.Deny("Không cho phép truy cập mạng nội bộ hoặc metadata.");
        }

        // Only allow HTTP/HTTPS
        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            return PathValidationResult.Deny($"Scheme '{uri.Scheme}' không được hỗ trợ. Chỉ hỗ trợ http/https.");
        }

        return PathValidationResult.Allow(url);
    }

    /// <summary>
    /// Reset resource counters for a new request.
    /// Call this at the start of each user request.
    /// </summary>
    public void ResetForNewRequest()
    {
        lock (_countLock)
        {
            _currentToolCallCount = 0;
        }
    }

    /// <summary>
    /// Sanitize tool output: limit length, remove sensitive patterns, cap lines.
    /// </summary>
    private string SanitizeToolOutput(string output, string toolName)
    {
        if (string.IsNullOrEmpty(output)) return output;

        // Remove any accidentally exposed secrets/tokens
        output = System.Text.RegularExpressions.Regex.Replace(
            output,
            @"(?i)(api[-_]?key|secret[-_]?key|password|token|bearer)\s*[:=]\s*\S+",
            "$1=[REDACTED]");

        // Remove connection strings
        output = System.Text.RegularExpressions.Regex.Replace(
            output,
            @"(?i)(Server|Host|Data Source)=[^;]+;[^\n]+",
            "[CONNECTION_STRING_REDACTED]");

        // Limit number of lines
        var lines = output.Split('\n');
        if (lines.Length > MaxToolOutputLinesPerResult)
        {
            output = string.Join("\n", lines.Take(MaxToolOutputLinesPerResult))
                + $"\n... [Đã cắt bớt: còn {lines.Length - MaxToolOutputLinesPerResult} dòng nữa]";
        }

        // Limit total characters
        if (output.Length > MaxToolOutputChars)
        {
            output = output.Substring(0, MaxToolOutputChars)
                + $"\n... [Đã cắt bớt: output gốc {output.Length} ký tự]";
            _logger.LogInformation("🔒 [Sandbox] Truncated '{Tool}' output from {Original} to {Max} chars",
                toolName, output.Length, MaxToolOutputChars);
        }

        return output;
    }

    /// <summary>
    /// Sanitize error messages before returning to the AI.
    /// Don't leak stack traces, file paths, or internal details.
    /// </summary>
    private string SanitizeErrorMessage(Exception ex)
    {
        // Map common exception types to safe messages
        return ex switch
        {
            FileNotFoundException => "File không tồn tại hoặc đã bị xóa.",
            UnauthorizedAccessException => "Không có quyền truy cập file/thư mục.",
            IOException => "Lỗi I/O khi đọc/ghi file.",
            HttpRequestException => "Lỗi kết nối mạng.",
            TimeoutException => "Hết thời gian chờ.",
            ArgumentException => "Tham số không hợp lệ.",
            _ => $"Lỗi nội bộ khi thực thi công cụ. (Type: {ex.GetType().Name})"
        };
    }
}

// ── Result Models ──

public class SandboxResult
{
    public bool IsSuccess { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsTimedOut { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    public static SandboxResult Success(string tool, string output)
        => new() { IsSuccess = true, ToolName = tool, Output = output };
    public static SandboxResult Blocked(string tool, string reason)
        => new() { IsBlocked = true, ToolName = tool, ErrorMessage = reason };
    public static SandboxResult TimedOut(string tool, TimeSpan timeout)
        => new() { IsTimedOut = true, ToolName = tool, ErrorMessage = $"Hết thời gian ({timeout.TotalSeconds}s)" };
    public static SandboxResult Error(string tool, string message)
        => new() { ToolName = tool, ErrorMessage = message };
}

public class PathValidationResult
{
    public bool IsAllowed { get; set; }
    public string? DenialReason { get; set; }
    public string SanitizedPath { get; set; } = string.Empty;

    public static PathValidationResult Allow(string path)
        => new() { IsAllowed = true, SanitizedPath = path };
    public static PathValidationResult Deny(string reason)
        => new() { IsAllowed = false, DenialReason = reason };
}
