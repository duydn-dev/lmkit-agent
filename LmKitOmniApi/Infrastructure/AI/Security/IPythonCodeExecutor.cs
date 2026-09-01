namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Executes untrusted Python in an OS-isolated container sandbox. Unlike the
/// in-process Jint JavaScript engine (<see cref="IExecutionSandboxEngine"/>),
/// Python has full system access, so isolation MUST happen at the container
/// boundary (no network, non-root, dropped capabilities, read-only rootfs,
/// CPU/memory/pids/time limits) — never in-process.
/// </summary>
public interface IPythonCodeExecutor
{
    /// <summary>
    /// True only when the interpreter is enabled AND a runtime image is
    /// configured. The orchestrator uses this to decide whether to offer the
    /// run_python tool; when false, callers must not execute and should surface
    /// the "not configured" path.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Runs <paramref name="code"/> in the sandbox and returns captured
    /// stdout/stderr (capped). Never throws for script-level failures — a
    /// non-zero exit, a timeout, an over-limit script, or a disabled interpreter
    /// all come back as a bracketed, agent-readable message (treated as untrusted
    /// tool output downstream).
    /// </summary>
    Task<string> ExecuteAsync(string code, CancellationToken ct = default);
}
