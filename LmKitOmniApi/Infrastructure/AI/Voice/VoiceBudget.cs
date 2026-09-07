namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Runs one asynchronous step under a wall-clock budget WITHOUT ever blocking a thread and
/// without turning a budget overrun into an exception the caller has to sort out from a real
/// shutdown.
///
/// Why this exists: every wait in the voice dispatcher touches a scarce, process-wide resource
/// (the single chat/speech inference leases in <c>LmModelManager</c>, or a native LiveKit read).
/// A wait that cannot expire is how one wedged room starves the whole process, so every await
/// in a dispatched session goes through here.
///
/// Semantics:
/// <list type="bullet">
///   <item>Returns <c>Completed=true</c> plus the value when the step finished in time.</item>
///   <item>Returns <c>Completed=false</c> when the BUDGET expired — the step is abandoned with
///   its own token already cancelled, and any later fault is observed so it can never surface
///   as an unobserved task exception.</item>
///   <item>Rethrows <see cref="OperationCanceledException"/> when the OUTER token was cancelled,
///   so shutdown is never mistaken for a timeout.</item>
/// </list>
/// The linked <see cref="CancellationTokenSource"/> is disposed only once the step it was handed
/// to has actually finished; disposing it while an abandoned step still holds the token would
/// turn a timeout into an <see cref="ObjectDisposedException"/> inside that step.
/// </summary>
internal static class VoiceBudget
{
    /// <summary>True when the budget means "no limit".</summary>
    internal static bool IsUnbounded(TimeSpan budget) =>
        budget == Timeout.InfiniteTimeSpan || budget <= TimeSpan.Zero;

    internal static async Task<(bool Completed, T? Value)> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan budget,
        CancellationToken ct)
    {
        if (IsUnbounded(budget))
            return (true, await operation(ct).ConfigureAwait(false));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        Task<T> work;
        try
        {
            work = operation(cts.Token);
        }
        catch (OperationCanceledException)
        {
            cts.Dispose();
            ct.ThrowIfCancellationRequested();
            return (false, default);
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        // Completes (as Canceled) the moment the budget expires or the outer token is cancelled.
        var expiry = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        var first = await Task.WhenAny(work, expiry).ConfigureAwait(false);

        if (ReferenceEquals(first, work))
        {
            try
            {
                var value = await work.ConfigureAwait(false);
                return (true, value);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The step observed the budget itself and bowed out — same outcome as a timeout.
                return (false, default);
            }
            finally
            {
                cts.Cancel();   // release the expiry task's timer registration
                cts.Dispose();
            }
        }

        // Budget expired (or the outer token was cancelled). The step keeps its cancelled token;
        // dispose the source only once it has actually unwound.
        Abandon(work, cts);
        ct.ThrowIfCancellationRequested();
        return (false, default);
    }

    /// <summary>Void-returning overload; <c>Completed</c> is the whole answer.</summary>
    internal static async Task<bool> RunAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan budget,
        CancellationToken ct)
    {
        var (completed, _) = await RunAsync<bool>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, budget, ct).ConfigureAwait(false);
        return completed;
    }

    /// <summary>
    /// Lets an abandoned step finish on its own: observes any fault (so it is never an
    /// unobserved task exception) and only then disposes the token source it is still using.
    /// </summary>
    private static void Abandon<T>(Task<T> work, CancellationTokenSource cts)
    {
        _ = work.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception; // observe
                ((CancellationTokenSource)state!).Dispose();
            },
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
