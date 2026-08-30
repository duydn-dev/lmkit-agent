

using Jint;
using Jint.Constraints;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Security;

public interface IExecutionSandboxEngine
{
    Task<string> ExecuteCodeSafelyAsync(string codeSnippet, string language, CancellationToken ct = default);
}

/// <summary>
/// v1 code interpreter: JavaScript only, executed in a hard-capped Jint sandbox
/// (4MB RAM / 2s / 10k statements — no CLR interop, no network, no filesystem).
/// Hardening on top of the raw engine:
///  - console.log/info/warn/error/debug are captured into a bounded buffer
///    (max 100 lines / 4,000 chars) and prepended to the result as
///    "console output\n---\nresult";
///  - the result string is capped at 8,000 chars with an explicit marker so a
///    script can never flood the agent context;
///  - the blocking Evaluate runs on a worker thread and honors the caller's
///    CancellationToken (Jint cancellation constraint + post-run check);
///  - undefined/null results yield "(không có giá trị trả về)" instead of an
///    empty/confusing string; object results are JSON-stringified.
/// The console bridge crosses the boundary as a single string-appending
/// delegate — no CLR object is ever exposed to the script and AllowClr is
/// never enabled, so System.* / importNamespace stay undefined.
/// </summary>
public class ExecutionSandboxEngine : IExecutionSandboxEngine
{
    // ── Engine resource limits (unchanged contract: 4MB / 2s / 10k statements) ──
    private const long MemoryLimitBytes = 4 * 1024 * 1024;
    private const int MaxStatementCount = 10_000;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    // ── Output budgets (context-flood protection) ──
    private const int MaxResultChars = 8_000;
    private const int MaxConsoleLines = 100;
    private const int MaxConsoleChars = 4_000;
    private const int MaxErrorMessageChars = 500;

    private const string NoReturnValueMessage = "(không có giá trị trả về)";

    /// <summary>
    /// Installs a capturing `console` whose calls funnel into the host's bounded
    /// buffer through a single (string level, string message) delegate. The
    /// delegate global is cleared afterwards; even called directly it can only
    /// append to the bounded buffer. Objects are JSON-stringified per argument.
    /// </summary>
    private const string ConsoleShim = """
        (function () {
            var write = globalThis.__hermesConsoleWrite;
            globalThis.__hermesConsoleWrite = undefined;
            function format(args) {
                var parts = [];
                for (var i = 0; i < args.length; i++) {
                    var value = args[i];
                    if (typeof value === 'object' && value !== null) {
                        try { parts.push(JSON.stringify(value)); continue; } catch (e) { }
                    }
                    parts.push(String(value));
                }
                return parts.join(' ');
            }
            globalThis.console = {
                log: function () { write('log', format(arguments)); },
                info: function () { write('info', format(arguments)); },
                warn: function () { write('warn', format(arguments)); },
                error: function () { write('error', format(arguments)); },
                debug: function () { write('debug', format(arguments)); }
            };
        })();
        """;

    private readonly ILogger<ExecutionSandboxEngine> _logger;

    public ExecutionSandboxEngine(ILogger<ExecutionSandboxEngine> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExecuteCodeSafelyAsync(string codeSnippet, string language, CancellationToken ct = default)
    {
        if (!string.Equals(language, "javascript", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(language, "js", StringComparison.OrdinalIgnoreCase))
        {
            return "[Sandbox Error] Chỉ hỗ trợ thực thi an toàn ngôn ngữ JavaScript (qua Jint).";
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("🚀 Bắt đầu thực thi JavaScript trong Sandbox...");

            // engine.Evaluate is a blocking, CPU-bound call: keep it off the
            // caller's async path. Runtime is bounded by the Jint timeout /
            // statement / memory constraints plus the cancellation constraint.
            var output = await Task.Run(() => RunSandboxed(codeSnippet, ct), ct);
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("✅ Sandbox thực thi thành công.");
            return output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JavaScriptException ex)
        {
            // Script-level failure (ReferenceError, thrown Error, ...). Surfacing
            // the JS message lets the ReAct model correct its own code; it flows
            // back as tool output, which the agent already treats as untrusted.
            _logger.LogWarning("❌ Lỗi Sandbox (JavaScript): {Error}", ex.Message);
            return $"[Sandbox Error] Lỗi JavaScript: {TrimErrorMessage(ex.Message)}";
        }
        catch (JintException ex)
        {
            // Parse/engine-level failure. Jint 4 keeps SyntaxErrorException
            // internal, so the public base covers syntax errors here.
            _logger.LogWarning("❌ Lỗi Sandbox (cú pháp/engine): {Error}", ex.Message);
            return $"[Sandbox Error] Lỗi cú pháp JavaScript: {TrimErrorMessage(ex.Message)}";
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("JavaScript sandbox execution was canceled.", ex, ct);

            // Resource-limit trips (timeout / memory / statement count) and any
            // engine fault deliberately collapse into one generic message.
            _logger.LogWarning("❌ Lỗi Sandbox: {Error}", ex.Message);
            return "[Sandbox Error] JavaScript execution failed or exceeded its resource limits.";
        }
    }

    /// <summary>Runs entirely on the worker thread — the Engine is single-threaded.</summary>
    private static string RunSandboxed(string codeSnippet, CancellationToken ct)
    {
        var consoleBuffer = new BoundedConsoleBuffer(MaxConsoleLines, MaxConsoleChars);

        // Thiết lập Sandbox an toàn tuyệt đối
        using var engine = new Engine(options =>
        {
            options.LimitMemory(MemoryLimitBytes);      // Giới hạn 4MB RAM
            options.TimeoutInterval(ExecutionTimeout);  // Quá 2s là kill
            options.MaxStatements(MaxStatementCount);   // Tránh vòng lặp vô tận
            options.CancellationToken(ct);              // Hủy giữa chừng theo caller
        });

        // Console bridge: only a string-appending delegate crosses the boundary.
        engine.SetValue("__hermesConsoleWrite", new Action<string, string>(consoleBuffer.Write));
        engine.Execute(ConsoleShim);

        // Chạy script — the completion value of the LAST EXPRESSION is the result.
        var value = engine.Evaluate(codeSnippet);
        ct.ThrowIfCancellationRequested();

        var resultText = CapResult(FormatResult(engine, value));
        return consoleBuffer.HasOutput
            ? $"{consoleBuffer}\n---\n{resultText}"
            : resultText;
    }

    /// <summary>
    /// undefined/null → explicit notice; objects/arrays → JSON (falling back to
    /// String(v) on cyclic structures); everything else → its JS string form.
    /// Rendering runs inside the same constrained engine, so hostile getters or
    /// toString overrides stay subject to the sandbox limits.
    /// </summary>
    private static string FormatResult(Engine engine, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
            return NoReturnValueMessage;

        engine.SetValue("__hermesResult", value);
        var rendered = engine.Evaluate("""
            (function (v) {
                if (v === undefined || v === null) return undefined;
                if (typeof v === 'object') {
                    try {
                        var json = JSON.stringify(v);
                        if (json !== undefined) return json;
                    } catch (e) { }
                }
                return String(v);
            })(__hermesResult)
            """);

        return rendered.IsUndefined() ? NoReturnValueMessage : rendered.ToString();
    }

    private static string CapResult(string result) =>
        result.Length <= MaxResultChars
            ? result
            : result[..MaxResultChars] + $"\n[Kết quả đã bị cắt bớt vì vượt quá {MaxResultChars} ký tự]";

    private static string TrimErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(không có thông tin lỗi)";
        var singleLine = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= MaxErrorMessageChars
            ? singleLine
            : singleLine[..MaxErrorMessageChars] + "…";
    }

    /// <summary>
    /// Bounded line buffer backing the captured `console`. Written only from the
    /// engine's worker thread; read by the caller after evaluation completes.
    /// Once either bound trips, a single truncation marker is appended and all
    /// further writes are ignored.
    /// </summary>
    private sealed class BoundedConsoleBuffer
    {
        private const string TruncationMarker = "[console: vượt giới hạn bộ đệm, các dòng tiếp theo đã bị bỏ qua]";

        private readonly int _maxLines;
        private readonly int _maxChars;
        private readonly System.Text.StringBuilder _text = new();
        private int _lineCount;
        private bool _truncated;

        public BoundedConsoleBuffer(int maxLines, int maxChars)
        {
            _maxLines = maxLines;
            _maxChars = maxChars;
        }

        public bool HasOutput => _text.Length > 0;

        public void Write(string level, string message)
        {
            if (_truncated) return;

            if (_lineCount >= _maxLines || _text.Length >= _maxChars)
            {
                Truncate();
                return;
            }

            var line = level is "log" ? message : $"[{level}] {message}";
            var remaining = _maxChars - _text.Length;
            if (line.Length > remaining)
            {
                _text.Append(line, 0, remaining).Append('\n');
                Truncate();
                return;
            }

            _text.Append(line).Append('\n');
            _lineCount++;
        }

        private void Truncate()
        {
            _truncated = true;
            _text.Append(TruncationMarker);
        }

        public override string ToString() => _text.ToString().TrimEnd('\n');
    }
}
