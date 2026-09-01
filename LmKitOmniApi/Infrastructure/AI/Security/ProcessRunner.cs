using System.Diagnostics;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// A deliberately thin seam: it launches the executable with an argument LIST
/// (never a joined shell string, so nothing is shell-interpreted), drains stdout
/// and stderr concurrently to avoid the classic redirected-pipe deadlock, feeds
/// stdin if supplied, and enforces a hard wall-clock budget by killing the whole
/// process tree.
///
/// Contract notes:
///  - It NEVER caps output and NEVER throws on a non-zero exit — those are normal
///    results the caller (the code executor) interprets and caps.
///  - A wall-clock overrun is reported as <see cref="ProcessRunResult.TimedOut"/> =
///    true (not an exception).
///  - The caller's <paramref name="ct"/> being cancelled propagates as an
///    <see cref="OperationCanceledException"/> (after killing the child).
///  - Only genuinely-exceptional launch failures (e.g. the runtime executable is
///    missing → <see cref="System.ComponentModel.Win32Exception"/>) surface as
///    exceptions; the executor catches those and returns a safe message.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Argument LIST — each token added verbatim, never concatenated into a
        // shell string, so a value like "; rm -rf /" can never be interpreted.
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        // One linked source fires on EITHER the caller cancelling OR the wall-clock
        // budget elapsing; we tell the two apart afterwards via ct.IsCancellationRequested.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        // May throw synchronously (e.g. Win32Exception when the runtime is missing).
        // As this method is async, that surfaces as a faulted task the caller awaits.
        process.Start();

        // Read both pipes concurrently and to completion. No token here on purpose:
        // the reads finish when the pipes reach EOF, which the kill below guarantees
        // by tearing down the child (and its tree), so we still capture whatever the
        // process emitted before it was stopped.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Feed stdin if provided, then always close it so the child sees EOF and
        // never blocks waiting for input. Best-effort: the child may have already
        // exited (broken pipe) or the run may be cancelled mid-write.
        try
        {
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), linkedCts.Token);
        }
        catch
        {
            // Non-fatal: stdin delivery failures must not fail the whole run.
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch { /* already torn down */ }
        }

        var timedOut = false;
        var cancelledByCaller = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // The child overran its budget, or the caller cancelled. Either way,
            // kill the whole tree so no orphaned grandchildren keep running (or hold
            // the pipes open). A caller-cancel propagates; a timeout is a result.
            KillProcessTree(process);
            cancelledByCaller = ct.IsCancellationRequested;
            timedOut = !cancelledByCaller;
        }

        // Pipes are closed now (clean exit or kill) so these complete promptly.
        var stdOut = await ReadQuietlyAsync(stdOutTask);
        var stdErr = await ReadQuietlyAsync(stdErrTask);

        if (cancelledByCaller)
            ct.ThrowIfCancellationRequested();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return new ProcessRunResult(exitCode, stdOut, stdErr, timedOut);
    }

    private static async Task<string> ReadQuietlyAsync(Task<string> readTask)
    {
        try { return await readTask; }
        catch { return string.Empty; }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or we lack rights to signal it — nothing to do.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }
}
