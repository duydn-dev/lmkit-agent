namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Outcome of an external process invocation. <see cref="TimedOut"/> is true when
/// the process was killed for exceeding its wall-clock budget (ExitCode is then
/// unspecified).
/// </summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Thin, mockable seam over launching an external process (e.g. <c>docker run</c>).
/// Exists so the container code-interpreter's command construction, output
/// capping and timeout handling can be unit-tested without a real container
/// runtime. The default implementation shells out via System.Diagnostics.Process.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> (passed
    /// as an argument list — never a joined shell string — so nothing is
    /// shell-interpreted), feeding <paramref name="stdin"/> if provided, and kills
    /// the process if it runs longer than <paramref name="timeout"/>.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin,
        TimeSpan timeout,
        CancellationToken ct);
}
