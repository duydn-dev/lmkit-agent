namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Configuration for the container-backed Python code interpreter. Bound from the
/// "CodeInterpreter:Python" configuration section. DISABLED BY DEFAULT: arbitrary
/// Python has full system access, so it only runs when an operator explicitly
/// enables it AND provisions a container runtime (a locked-down docker/OCI runner
/// reachable from the API). When disabled, the run_python tool is never offered to
/// the agent and any invocation returns a safe "not configured" message.
/// </summary>
public sealed class CodeInterpreterOptions
{
    public const string SectionName = "CodeInterpreter:Python";

    /// <summary>Master switch. False (default) = the Python interpreter is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Toggle for the in-process JavaScript (Jint) code interpreter, surfaced on the JS
    /// execution seam as <see cref="IExecutionSandboxEngine.IsEnabled"/>. TRUE by default,
    /// which PRESERVES existing behavior: unlike <see cref="Enabled"/> (the container-backed
    /// Python interpreter, off by default because arbitrary Python has full system access),
    /// Jint is a hard-capped in-process sandbox — no CLR, no network, no filesystem — so this
    /// switch is about honesty / operator controllability, not a security hole. When false,
    /// the run_javascript tool is not offered and any invocation returns a safe "not enabled"
    /// message (mirroring the Python path when off). Bound from
    /// "CodeInterpreter:Python:JavaScriptEnabled" (this options object's section).
    /// </summary>
    public bool JavaScriptEnabled { get; set; } = true;

    /// <summary>Container image with a Python runtime, e.g. "python:3.12-alpine".</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Container runtime executable (default "docker").</summary>
    public string RuntimePath { get; set; } = "docker";

    /// <summary>Hard wall-clock limit per execution (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Memory ceiling passed to the runtime (MB).</summary>
    public int MemoryMb { get; set; } = 256;

    /// <summary>CPU quota passed to the runtime (e.g. 1.0 = one core).</summary>
    public double Cpus { get; set; } = 1.0;

    /// <summary>Max characters of combined stdout/stderr returned to the agent.</summary>
    public int MaxOutputChars { get; set; } = 8_000;

    /// <summary>Max characters of the submitted script (reject larger).</summary>
    public int MaxScriptChars { get; set; } = 20_000;

    /// <summary>Max number of produced files returned to the user per run (0 disables file return).</summary>
    public int MaxOutputFiles { get; set; } = 5;

    /// <summary>Max size of a single produced file that is returned (bytes); larger files are skipped.</summary>
    public long MaxOutputFileBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Max combined size of all produced files returned per run (bytes).</summary>
    public long MaxTotalOutputFileBytes { get; set; } = 15 * 1024 * 1024;
}
