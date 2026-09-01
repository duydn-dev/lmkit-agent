namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// A file the interpreter produced under its /work scratch during a run and that
/// has been persisted into the caller's isolated upload root so it can be served
/// back to the user. <see cref="Id"/> is the on-disk (server-generated) name used
/// by the file-download endpoint; <see cref="Name"/> is the original name the
/// script wrote, shown/downloaded in the UI.
/// </summary>
public sealed record ProducedFile(string Id, string Name, string ContentType, long SizeBytes);

/// <summary>
/// Result of a Python run: the captured stdout/stderr text (the agent-visible
/// observation) plus any files the script produced and that were persisted for
/// return to the user.
/// </summary>
public sealed record PythonExecutionResult(string Output, IReadOnlyList<ProducedFile> Files)
{
    public static PythonExecutionResult TextOnly(string output) => new(output, System.Array.Empty<ProducedFile>());
}

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
    /// stdout/stderr (capped) plus any files the script wrote to /work, persisted
    /// under the (<paramref name="tenantId"/>, <paramref name="userId"/>) upload
    /// root for return to the user. Never throws for script-level failures — a
    /// non-zero exit, a timeout, an over-limit script, or a disabled interpreter
    /// all come back as a bracketed, agent-readable message with no files (treated
    /// as untrusted tool output downstream).
    /// </summary>
    Task<PythonExecutionResult> ExecuteAsync(string code, Guid tenantId, Guid userId, CancellationToken ct = default);
}
