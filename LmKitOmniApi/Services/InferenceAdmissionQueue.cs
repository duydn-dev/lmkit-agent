using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LmKitOmniApi.Services;

/// <summary>
/// Why a caller was turned away instead of being given an inference permit.
/// </summary>
public enum InferenceQueueRejectionReason
{
    /// <summary>More callers were already waiting than <see cref="InferenceQueueOptions.MaxQueueDepth"/> allows.</summary>
    QueueFull,

    /// <summary>The caller reached <see cref="InferenceQueueOptions.MaxWaitSeconds"/> without reaching the front.</summary>
    WaitTimeout,
}

/// <summary>
/// Thrown by <see cref="InferenceAdmissionQueue.AcquireAsync"/> when the deployment cannot
/// serve the caller in a defensible amount of time.
///
/// Deliberately NOT an <see cref="OperationCanceledException"/>: several call sites (e.g.
/// <c>DeepResearchService.TryAcquireChatLeaseAsync</c>) catch OCE and quietly degrade, which
/// is right for "the caller's own budget expired" and wrong for "the server is over
/// capacity" — the second needs to reach the user as an explanation, not a silent no-op.
/// A caller's own cancellation still surfaces as a plain OCE exactly as it did before.
/// </summary>
public sealed class InferenceQueueRejectedException : Exception
{
    internal InferenceQueueRejectedException(
        string gate,
        InferenceQueueRejectionReason reason,
        TimeSpan waited,
        int queueDepth,
        string message)
        : base(message)
    {
        Gate = gate;
        Reason = reason;
        Waited = waited;
        QueueDepth = queueDepth;
    }

    /// <summary>Which inference gate refused ("chat").</summary>
    public string Gate { get; }

    public InferenceQueueRejectionReason Reason { get; }

    /// <summary>How long this caller actually waited before being turned away.</summary>
    public TimeSpan Waited { get; }

    /// <summary>Callers waiting on the gate when the decision was taken.</summary>
    public int QueueDepth { get; }
}

/// <summary>
/// Bounds and reporting thresholds for one inference gate. Every value is configurable;
/// the defaults are the ones this type documents and are what ships when the
/// <c>InferenceQueue</c> section is absent from appsettings.
/// </summary>
public sealed class InferenceQueueOptions
{
    /// <summary>Waiters allowed to queue behind the permit holders. 0 disables the bound.</summary>
    public const int DefaultMaxQueueDepth = 32;

    /// <summary>Longest a caller may wait for a permit. 0 disables the bound (pre-queue behaviour).</summary>
    public const int DefaultMaxWaitSeconds = 300;

    /// <summary>
    /// A wait shorter than this emits NO progress notice at all. This is what keeps the
    /// uncontended, single-user byte stream identical to the pre-queue one.
    /// </summary>
    public const int DefaultNoticeAfterMilliseconds = 750;

    /// <summary>Spacing between the second and subsequent progress notices.</summary>
    public const int DefaultNoticeIntervalSeconds = 5;

    public int MaxQueueDepth { get; init; } = DefaultMaxQueueDepth;
    public int MaxWaitSeconds { get; init; } = DefaultMaxWaitSeconds;
    public int NoticeAfterMilliseconds { get; init; } = DefaultNoticeAfterMilliseconds;
    public int NoticeIntervalSeconds { get; init; } = DefaultNoticeIntervalSeconds;

    internal TimeSpan MaxWait => MaxWaitSeconds <= 0
        ? Timeout.InfiniteTimeSpan
        : TimeSpan.FromSeconds(MaxWaitSeconds);

    internal TimeSpan NoticeAfter => TimeSpan.FromMilliseconds(NoticeAfterMilliseconds);

    internal TimeSpan NoticeInterval => TimeSpan.FromSeconds(NoticeIntervalSeconds);

    /// <summary>
    /// Binds <c>{sectionPath}</c> (e.g. <c>InferenceQueue:Chat</c>). A missing section yields
    /// the documented defaults, so the shipped appsettings can stay silent about it.
    /// </summary>
    public static InferenceQueueOptions FromConfiguration(IConfiguration configuration, string sectionPath)
    {
        var section = configuration.GetSection(sectionPath);
        var options = new InferenceQueueOptions
        {
            MaxQueueDepth = section.GetValue("MaxQueueDepth", DefaultMaxQueueDepth),
            MaxWaitSeconds = section.GetValue("MaxWaitSeconds", DefaultMaxWaitSeconds),
            NoticeAfterMilliseconds = section.GetValue("NoticeAfterMilliseconds", DefaultNoticeAfterMilliseconds),
            NoticeIntervalSeconds = section.GetValue("NoticeIntervalSeconds", DefaultNoticeIntervalSeconds),
        };
        options.Validate(sectionPath);
        return options;
    }

    internal void Validate(string sectionPath)
    {
        if (MaxQueueDepth < 0)
            throw new InvalidOperationException($"{sectionPath}:MaxQueueDepth must be zero (unbounded) or greater.");
        if (MaxWaitSeconds < 0)
            throw new InvalidOperationException($"{sectionPath}:MaxWaitSeconds must be zero (unbounded) or greater.");
        if (NoticeAfterMilliseconds < 0)
            throw new InvalidOperationException($"{sectionPath}:NoticeAfterMilliseconds must be zero or greater.");
        if (NoticeIntervalSeconds < 1)
            throw new InvalidOperationException($"{sectionPath}:NoticeIntervalSeconds must be at least 1.");
    }
}

/// <summary>In-process counters for a gate; the same numbers the meter publishes.</summary>
public readonly record struct InferenceQueueSnapshot(
    int Depth,
    long TotalAdmitted,
    long TotalQueuedAdmissions,
    long TotalRejections,
    double TotalWaitMilliseconds);

/// <summary>
/// A bounded, observable wait in front of an inference permit.
///
/// <para><b>What this is not.</b> It is not a scheduler and not a replacement for the
/// permit primitive. The permit is still a <see cref="SemaphoreSlim"/> owned by
/// <see cref="LmModelManager"/>; this type only decides who is allowed to wait for it, for
/// how long, and what the caller is told meanwhile. Ordering therefore remains
/// SemaphoreSlim's: its async waiters are a FIFO linked list and <c>Release</c> hands the
/// permit to the head under its own lock, so a newcomer's <c>Wait(0)</c> fast path cannot
/// barge past a queue (the count is zero whenever waiters are parked).
/// <c>InferenceAdmissionQueueTests.QueuedCallers_AreGrantedInArrivalOrder</c> pins that
/// ordering, because <see cref="SemaphoreSlim"/> documents no ordering guarantee at all.</para>
///
/// <para><b>Release discipline.</b> A permit is handed out in exactly one place
/// (<see cref="InferenceAdmission"/>) and given back in exactly one place
/// (<c>InferenceAdmission.DisposeAsync</c>). The one genuinely dangerous case is
/// SemaphoreSlim's cancellation race: when a <c>Release</c> and a token fire together,
/// <c>WaitAsync</c> returns SUCCESSFULLY with a cancelled token — so a naive
/// "if (timedOut) throw" after the await silently loses a permit and deadlocks the product.
/// Nothing here throws between the await completing and the permit being recorded, and
/// <c>DisposeAsync</c> re-awaits an abandoned wait specifically to give back a permit that
/// arrived after the caller walked away.</para>
/// </summary>
public sealed class InferenceAdmissionQueue : IDisposable
{
    // Published on the meter Program.cs already exports ("LmKitOmniApi.AgentMetrics"), so
    // these instruments reach Prometheus/OTLP with no startup change. Instruments are
    // static so constructing many managers (the test suite does) cannot accumulate them.
    private static readonly Meter Meter = new("LmKitOmniApi.AgentMetrics", "1.0.0");

    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>(
        "inference_queue_wait_ms", "ms", "Time a caller spent waiting for an inference permit");

    private static readonly Counter<long> Admissions = Meter.CreateCounter<long>(
        "inference_queue_admissions_total", description: "Inference permits granted");

    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>(
        "inference_queue_rejections_total", description: "Inference requests refused by the queue bound");

    private static readonly UpDownCounter<long> QueueDepthGauge = Meter.CreateUpDownCounter<long>(
        "inference_queue_depth", description: "Callers currently waiting for an inference permit");

    private readonly SemaphoreSlim _gate;
    private readonly string _gateName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;

    // Cancelling is the CLEAN way out for a parked waiter, but it is not sufficient on its
    // own: SemaphoreSlim.Dispose() nulls its waiter list WITHOUT completing the parked nodes,
    // so a waiter that is still unwinding when LmModelManager disposes the gate is orphaned
    // and its task never completes. This signal is the escape hatch — every wait races it, so
    // shutdown can never turn into a hang no matter which side wins.
    private readonly TaskCompletionSource _shutdownSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly KeyValuePair<string, object?> _gateTag;

    private int _waiting;
    private long _ticketSequence;
    private long _departedCount;
    private long _totalAdmitted;
    private long _totalQueuedAdmissions;
    private long _totalRejections;
    private long _totalWaitTicks;

    // Test seam: incremented the instant a waiter is parked on the semaphore, which is the
    // only observable moment at which its FIFO position is fixed.
    internal long EnqueuedWaiterCount;

    public InferenceAdmissionQueue(
        SemaphoreSlim gate,
        string gateName,
        InferenceQueueOptions options,
        ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _gateName = gateName;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _gateTag = new KeyValuePair<string, object?>("gate", gateName);
        // Captured once: CancellationTokenSource.Token throws after Dispose, and this token
        // is read from an exception handler where a second throw would lose the first.
        _shutdownToken = _shutdown.Token;
    }

    public InferenceQueueOptions Options { get; }

    internal SemaphoreSlim Gate => _gate;

    internal string GateName => _gateName;

    internal CancellationToken ShutdownToken => _shutdownToken;

    /// <summary>Completes once <see cref="SignalShutdown"/> has run. Shutdown is terminal.</summary>
    internal Task ShutdownTask => _shutdownSignal.Task;

    /// <summary>Callers currently waiting (holders excluded).</summary>
    public int QueueDepth => Volatile.Read(ref _waiting);

    public InferenceQueueSnapshot GetSnapshot() => new(
        QueueDepth,
        Interlocked.Read(ref _totalAdmitted),
        Interlocked.Read(ref _totalQueuedAdmissions),
        Interlocked.Read(ref _totalRejections),
        TimeSpan.FromTicks(Interlocked.Read(ref _totalWaitTicks)).TotalMilliseconds);

    /// <summary>
    /// Single-shot acquisition, drop-in for the previous <c>gate.WaitAsync(ct)</c>: returns a
    /// lease that MUST be disposed, throws <see cref="OperationCanceledException"/> when the
    /// caller's own token fires, and <see cref="InferenceQueueRejectedException"/> when the
    /// configured bound is exceeded. Emits no progress notices — use
    /// <see cref="Begin"/> from a streaming path that can show them.
    /// </summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)
    {
        var admission = new InferenceAdmission(this, ct, emitNotices: false);
        try
        {
            // With notices off the enumerable yields nothing, so this drains to exactly the
            // wait itself — one implementation, no second copy of the release discipline.
            await foreach (var _ in admission.WaitForTurnAsync().ConfigureAwait(false))
            {
            }

            if (admission.Rejection is { } rejection) throw rejection;
            return admission;
        }
        catch
        {
            await admission.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Streaming acquisition. The returned admission is an <see cref="IAsyncDisposable"/>
    /// that owns the eventual permit — <c>await using</c> it FIRST, then enumerate
    /// <see cref="InferenceAdmission.WaitForTurnAsync"/>, so the permit is given back on
    /// every path including an abandoned enumeration.
    /// </summary>
    public InferenceAdmission Begin(CancellationToken ct = default) => new(this, ct, emitNotices: true);

    /// <summary>
    /// Unblocks every parked waiter so process shutdown cannot hang behind an in-flight
    /// inference. Called by <see cref="LmModelManager.Dispose"/> BEFORE the gate itself is
    /// disposed; <see cref="SemaphoreSlim.Dispose"/> does not wake its waiters.
    /// </summary>
    public void SignalShutdown()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "A waiter on the {Gate} inference queue threw while unwinding at shutdown.", _gateName);
        }
        finally
        {
            // Set even if Cancel threw: a waiter that never sees its token fire must still
            // have a way out before the gate itself is disposed underneath it.
            _shutdownSignal.TrySetResult();
        }
    }

    public void Dispose()
    {
        SignalShutdown();
        _shutdown.Dispose();
    }

    // ── Bookkeeping used by InferenceAdmission ───────────────────────────────

    internal int EnterQueue()
    {
        var depth = Interlocked.Increment(ref _waiting);
        QueueDepthGauge.Add(1, _gateTag);
        return depth;
    }

    internal void LeaveQueue()
    {
        Interlocked.Decrement(ref _waiting);
        Interlocked.Increment(ref _departedCount);
        QueueDepthGauge.Add(-1, _gateTag);
    }

    internal long NextTicket() => Interlocked.Increment(ref _ticketSequence);

    /// <summary>
    /// 1-based place in line for <paramref name="ticket"/>. Exact while the gate hands out
    /// permits in FIFO order, and monotonically improving in any case: every departure —
    /// granted, timed out or cancelled — advances the counter, so a caller behind an
    /// abandoned request is never pinned to a stale position.
    /// </summary>
    internal int PositionOf(long ticket)
    {
        var ahead = ticket - Interlocked.Read(ref _departedCount);
        return ahead < 1 ? 1 : (int)Math.Min(ahead, int.MaxValue);
    }

    internal void RecordAdmitted(TimeSpan waited, bool queued)
    {
        Interlocked.Increment(ref _totalAdmitted);
        if (queued)
        {
            Interlocked.Increment(ref _totalQueuedAdmissions);
            Interlocked.Add(ref _totalWaitTicks, waited.Ticks);
        }

        Admissions.Add(1, _gateTag, new KeyValuePair<string, object?>("path", queued ? "queued" : "immediate"));
        WaitDuration.Record(
            waited.TotalMilliseconds,
            _gateTag,
            new KeyValuePair<string, object?>("outcome", "granted"));

        if (queued)
        {
            _logger.LogInformation(
                "Inference permit for {Gate} granted after {WaitMs:F0}ms of queueing (depth now {Depth}).",
                _gateName,
                waited.TotalMilliseconds,
                QueueDepth);
        }
    }

    internal InferenceQueueRejectedException RecordRejection(
        InferenceQueueRejectionReason reason,
        TimeSpan waited,
        int queueDepth)
    {
        Interlocked.Increment(ref _totalRejections);
        Rejections.Add(1, _gateTag, new KeyValuePair<string, object?>("reason", reason.ToString()));
        WaitDuration.Record(
            waited.TotalMilliseconds,
            _gateTag,
            new KeyValuePair<string, object?>("outcome", "rejected"));

        var message = reason switch
        {
            InferenceQueueRejectionReason.QueueFull =>
                $"Máy đang phục vụ quá nhiều yêu cầu ({queueDepth} yêu cầu đang chờ). "
                + "Vui lòng thử lại sau ít phút.",
            _ =>
                $"Đã chờ {waited.TotalSeconds:F0}s mà vẫn chưa tới lượt xử lý vì máy đang bận. "
                + "Vui lòng thử lại sau.",
        };

        _logger.LogWarning(
            "Refused an inference request on {Gate}: {Reason} after {WaitMs:F0}ms with {Depth} waiting.",
            _gateName,
            reason,
            waited.TotalMilliseconds,
            queueDepth);

        return new InferenceQueueRejectedException(_gateName, reason, waited, queueDepth, message);
    }

    internal void ReleasePermit()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The manager was disposed underneath an in-flight lease. Nothing to give back.
        }
        catch (SemaphoreFullException ex)
        {
            // Would mean a double release — the failure mode this file exists to prevent.
            _logger.LogError(ex, "Double release detected on the {Gate} inference gate.", _gateName);
        }
    }
}

/// <summary>
/// One caller's place in the queue and, once granted, its permit.
///
/// <para>Lifetime: <c>await using</c> the admission, then enumerate
/// <see cref="WaitForTurnAsync"/>. The enumeration yields zero items when the permit is free
/// (the uncontended path), one progress notice once the wait passes
/// <see cref="InferenceQueueOptions.NoticeAfterMilliseconds"/> and one more every
/// <see cref="InferenceQueueOptions.NoticeIntervalSeconds"/> after that, and completes as
/// soon as the permit is granted or the bound is hit. On a bound it sets
/// <see cref="Rejection"/> rather than throwing, because C# forbids catching around a
/// <c>yield return</c> — a throwing wait could not be handled inside the streaming
/// iterator that needs to handle it.</para>
/// </summary>
public sealed class InferenceAdmission : IAsyncDisposable
{
    private readonly InferenceAdmissionQueue _queue;
    private readonly CancellationToken _callerToken;
    private readonly bool _emitNotices;

    private CancellationTokenSource? _linked;
    private Task? _waitTask;
    private long _startTimestamp;
    private long _ticket;
    private int _started;
    private int _queued;
    private int _held;
    private int _disposed;

    internal InferenceAdmission(InferenceAdmissionQueue queue, CancellationToken callerToken, bool emitNotices)
    {
        _queue = queue;
        _callerToken = callerToken;
        _emitNotices = emitNotices;
    }

    /// <summary>Non-null once the queue refused this caller; the message is user-facing.</summary>
    public InferenceQueueRejectedException? Rejection { get; private set; }

    /// <summary>True once the permit is held (and until this admission is disposed).</summary>
    public bool IsGranted => Volatile.Read(ref _held) == 1;

    /// <summary>How long this caller has waited so far.</summary>
    public TimeSpan Waited => _startTimestamp == 0
        ? TimeSpan.Zero
        : Stopwatch.GetElapsedTime(_startTimestamp);

    private enum WaitOutcome
    {
        Granted,
        Rejected,
        NoticeDue,
    }

    /// <summary>
    /// Waits for the permit, yielding a progress notice whenever the wait crosses a
    /// reporting threshold. Yields NOTHING when the permit is free.
    /// </summary>
    public async IAsyncEnumerable<string> WaitForTurnAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException(
                "InferenceAdmission.WaitForTurnAsync may only be enumerated once.");
        }

        // SemaphoreSlim.WaitAsync(ct) refuses an already-cancelled token even when a permit
        // is free; the fast path below would not. Keep the pre-queue behaviour.
        _callerToken.ThrowIfCancellationRequested();

        // ── Uncontended path ───────────────────────────────────────────────
        // Wait(0) is a non-blocking try. It succeeds only when the permit count is above
        // zero, which SemaphoreSlim guarantees is never the case while waiters are parked
        // (Release hands the permit straight to the queue head under its own lock), so this
        // cannot barge past a queue. No queue bookkeeping, no timer, no notice, no
        // suspension — the caller's ValueTask completes synchronously, exactly as
        // `await gate.WaitAsync(ct)` did on a free permit.
        if (_queue.Gate.Wait(0))
        {
            Volatile.Write(ref _held, 1);
            _queue.RecordAdmitted(TimeSpan.Zero, queued: false);
            yield break;
        }

        // ── Contended path ─────────────────────────────────────────────────
        _startTimestamp = Stopwatch.GetTimestamp();
        var depth = _queue.EnterQueue();
        Volatile.Write(ref _queued, 1);
        _ticket = _queue.NextTicket();

        var maxDepth = _queue.Options.MaxQueueDepth;
        if (maxDepth > 0 && depth > maxDepth)
        {
            LeaveQueueOnce();
            Rejection = _queue.RecordRejection(InferenceQueueRejectionReason.QueueFull, TimeSpan.Zero, depth);
            yield break;
        }

        if (!TryStartWait())
        {
            LeaveQueueOnce();
            throw new OperationCanceledException("The inference gate is shutting down.");
        }

        var nextNotice = _queue.Options.NoticeAfter;
        while (true)
        {
            TimeSpan? noticeIn = null;
            if (_emitNotices)
            {
                var remaining = nextNotice - Waited;
                noticeIn = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            var outcome = await AwaitTurnAsync(noticeIn).ConfigureAwait(false);
            if (outcome != WaitOutcome.NoticeDue) yield break;

            var elapsed = Waited;
            yield return FormatQueueNotice(elapsed);
            nextNotice = elapsed + _queue.Options.NoticeInterval;
        }
    }

    /// <summary>
    /// Parks this caller on the semaphore. Returns false only when the gate is already
    /// shutting down (the linked source is gone), which the caller reports as cancellation.
    /// </summary>
    private bool TryStartWait()
    {
        try
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, _queue.ShutdownToken);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        var maxWait = _queue.Options.MaxWait;
        if (maxWait != Timeout.InfiniteTimeSpan) _linked.CancelAfter(maxWait);

        // Assigning _waitTask publishes the parked waiter: WaitAsync links its node into the
        // semaphore's FIFO list synchronously before returning, so from here the position in
        // line is fixed and DisposeAsync is responsible for the permit it may receive.
        _waitTask = _queue.Gate.WaitAsync(_linked.Token);
        Interlocked.Increment(ref _queue.EnqueuedWaiterCount);
        return true;
    }

    /// <summary>
    /// Awaits the permit, optionally giving up early to let the caller emit a notice.
    /// All exception handling lives here rather than in the iterator, because C# forbids a
    /// <c>catch</c> around a <c>yield return</c>.
    /// </summary>
    private async Task<WaitOutcome> AwaitTurnAsync(TimeSpan? noticeIn)
    {
        var waitTask = _waitTask;
        if (waitTask is null) return WaitOutcome.Rejected;

        var shutdown = _queue.ShutdownTask;
        Task winner;
        if (noticeIn is { } delay)
        {
            using var delayCts = new CancellationTokenSource();
            var timer = Task.Delay(delay, delayCts.Token);
            winner = await Task.WhenAny(waitTask, shutdown, timer).ConfigureAwait(false);
            delayCts.Cancel(); // stop the timer; a cancelled Task.Delay needs no observation
            if (ReferenceEquals(winner, timer) && !waitTask.IsCompleted && !shutdown.IsCompleted)
                return WaitOutcome.NoticeDue;
        }
        else
        {
            winner = await Task.WhenAny(waitTask, shutdown).ConfigureAwait(false);
        }

        if (!waitTask.IsCompleted && shutdown.IsCompleted)
        {
            // Shutting down. ABANDON the parked wait rather than awaiting it:
            // SemaphoreSlim.Dispose() nulls its waiter list without completing the nodes, so
            // a node parked when the gate goes away never completes and `await waitTask`
            // would hang here forever — which is exactly how a clean shutdown becomes a
            // wedged process. Any permit that lands on the abandoned node belongs to a gate
            // that is being torn down, so there is nothing left to give back.
            _waitTask = null;
            LeaveQueueOnce();
            throw new OperationCanceledException("The inference gate is shutting down.");
        }

        try
        {
            await waitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SemaphoreSlim removes the waiter under its own lock BEFORE it reports
            // cancellation; if a Release had already been handed to this node the await
            // above returns normally instead. So reaching here means no permit was taken.
            _waitTask = null;
            LeaveQueueOnce();

            _callerToken.ThrowIfCancellationRequested();
            if (_queue.ShutdownToken.IsCancellationRequested) throw;

            // Disposed out from under an in-flight enumeration: that is an abandonment, not
            // a capacity problem, so it must not be reported (or counted) as a timeout.
            if (Volatile.Read(ref _disposed) == 1) throw;

            // Sampled here, AFTER the await: the elapsed time is the whole wait, which is the
            // number an operator needs to see on a rejection.
            Rejection = _queue.RecordRejection(
                InferenceQueueRejectionReason.WaitTimeout, Waited, _queue.QueueDepth);
            return WaitOutcome.Rejected;
        }
        catch (ObjectDisposedException)
        {
            _waitTask = null;
            LeaveQueueOnce();
            throw new OperationCanceledException("The inference gate was disposed while waiting.");
        }

        // Granted. Record the permit BEFORE anything that could throw, so no path between
        // here and DisposeAsync can lose it.
        Volatile.Write(ref _held, 1);
        var waited = Waited;
        _waitTask = null;
        LeaveQueueOnce();
        _queue.RecordAdmitted(waited, queued: true);
        return WaitOutcome.Granted;
    }

    /// <summary>
    /// The in-band progress notice. It rides the EXISTING <c>[THINKING]:</c> channel on
    /// purpose: that marker is already stripped by
    /// <c>StreamChatCommandHandler.StripProtocolMarkers</c> and already rendered by the
    /// client, so a queued turn needs no new marker, no new stripper and no client change.
    ///
    /// The trailing "\n" is a REAL newline (this is a normal, non-verbatim interpolated
    /// string, so the single backslash is an escape). A literal backslash-n here would put
    /// no newline on the wire and the line-anchored stripper
    /// (<c>\[THINKING\]:[^\n\r]+[\n\r]*</c>) would run straight through the answer that
    /// follows. <c>InferenceQueueMarkerTests</c> pins both halves of that.
    /// </summary>
    private string FormatQueueNotice(TimeSpan elapsed)
    {
        var position = _queue.PositionOf(_ticket);
        var depth = Math.Max(position, _queue.QueueDepth);
        return $"[THINKING]: ⏳ Máy đang bận — đang xếp hàng ở vị trí {position}/{depth} "
            + $"(đã chờ {(int)elapsed.TotalSeconds}s)\n";
    }

    private void LeaveQueueOnce()
    {
        if (Interlocked.Exchange(ref _queued, 0) == 1) _queue.LeaveQueue();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // The consumer stopped enumerating while still parked (client disconnect, an early
        // `break`, an exception upstream). Cancel, then AWAIT the wait: SemaphoreSlim can
        // hand a permit to a node that is cancelling at the same instant, and that permit
        // is ours to give back. Skipping this await is exactly how the single chat slot
        // would leak and wedge the deployment.
        var pending = Interlocked.Exchange(ref _waitTask, null);
        if (pending is not null)
        {
            try
            {
                _linked?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            // Bounded by the shutdown signal for the same reason the wait loop is: a node
            // parked when SemaphoreSlim.Dispose() ran is orphaned and never completes, and an
            // unconditional await here would hang disposal itself.
            await Task.WhenAny(pending, _queue.ShutdownTask).ConfigureAwait(false);

            if (pending.IsCompletedSuccessfully)
            {
                // The permit was handed over in the cancellation race — it is ours to return.
                Volatile.Write(ref _held, 1);
            }
            else if (pending.IsFaulted)
            {
                _ = pending.Exception; // observed; nothing was handed over
            }
        }

        if (Interlocked.Exchange(ref _held, 0) == 1) _queue.ReleasePermit();
        LeaveQueueOnce();
        _linked?.Dispose();
    }
}
