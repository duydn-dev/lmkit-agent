using System.Diagnostics;
using System.Diagnostics.Metrics;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Properties of the chat inference gate under contention.
///
/// <para>The gate is <c>SemaphoreLimits:Chat = 1</c> in production: every chat turn, agent
/// run, approved-tool resume and voice turn passes through it, so a leaked permit wedges the
/// whole deployment and an unbounded wait hangs an SSE connection with nothing on screen.
/// These tests pin the four things that matter — the permit count is never exceeded, the
/// permit comes back on EVERY exit path, the wait is bounded and fair, and the uncontended
/// single-caller path is untouched.</para>
///
/// <para>No test sleeps for a result. Waiting is always "poll a monotonic counter until a
/// condition holds, or fail the test", so a slow machine makes a test slower, never flaky.</para>
/// </summary>
public class InferenceAdmissionQueueTests
{
    private static LmModelManager BuildManager(
        int chatLimit = 1,
        int? maxWaitSeconds = null,
        int? maxQueueDepth = null,
        int? noticeAfterMilliseconds = null,
        int? noticeIntervalSeconds = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SemaphoreLimits:Chat"] = chatLimit.ToString(),
        };
        if (maxWaitSeconds is not null)
            settings["InferenceQueue:Chat:MaxWaitSeconds"] = maxWaitSeconds.Value.ToString();
        if (maxQueueDepth is not null)
            settings["InferenceQueue:Chat:MaxQueueDepth"] = maxQueueDepth.Value.ToString();
        if (noticeAfterMilliseconds is not null)
            settings["InferenceQueue:Chat:NoticeAfterMilliseconds"] = noticeAfterMilliseconds.Value.ToString();
        if (noticeIntervalSeconds is not null)
            settings["InferenceQueue:Chat:NoticeIntervalSeconds"] = noticeIntervalSeconds.Value.ToString();

        return new LmModelManager(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>Polls a condition instead of sleeping for a guessed duration.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string because, int timeoutMilliseconds = 30_000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds > timeoutMilliseconds)
                Assert.Fail($"Timed out after {timeoutMilliseconds}ms waiting for: {because}");
            await Task.Delay(2);
        }
    }

    /// <summary>Drains an admission, returning whatever queue notices it produced.</summary>
    private static async Task<List<string>> DrainAsync(InferenceAdmission admission)
    {
        var notices = new List<string>();
        await foreach (var notice in admission.WaitForTurnAsync())
            notices.Add(notice);
        return notices;
    }

    /// <summary>
    /// The permit is back if a fresh acquisition completes without ever suspending. This is
    /// a stronger leak check than "it eventually succeeded": a synchronously completed
    /// ValueTask can only come from the free-permit fast path.
    /// </summary>
    private static async Task AssertPermitIsFreeAsync(LmModelManager manager)
    {
        var acquisition = manager.AcquireChatInferenceAsync();
        Assert.True(
            acquisition.IsCompletedSuccessfully,
            "the chat permit was not free — a lease leaked on an earlier path");
        await using var _ = await acquisition;
    }

    // ── 1. The single-caller path is unchanged ───────────────────────────────

    /// <summary>
    /// The pre-queue implementation was <c>await gate.WaitAsync(ct); return lease;</c>, which
    /// on a free permit never suspends and so hands back an already-completed ValueTask. The
    /// queued implementation must do the same: no thread-pool hop, no timer, no allocation of
    /// queue state. This is the "same latency for one user" claim, asserted rather than argued.
    /// </summary>
    [Fact]
    public async Task Uncontended_Acquire_CompletesSynchronously_LikeTheDirectSemaphoreWait()
    {
        using var manager = BuildManager();

        var acquisition = manager.AcquireChatInferenceAsync();
        Assert.True(acquisition.IsCompletedSuccessfully);

        await using (await acquisition)
        {
            Assert.Equal(0, manager.ChatQueueDepth);
        }

        await AssertPermitIsFreeAsync(manager);
    }

    /// <summary>The uncontended stream is byte-identical: the admission yields NOTHING.</summary>
    [Fact]
    public async Task Uncontended_Admission_EmitsNoMarkersAtAll()
    {
        using var manager = BuildManager();

        await using var admission = manager.BeginChatInference();
        var notices = await DrainAsync(admission);

        Assert.Empty(notices);
        Assert.True(admission.IsGranted);
        Assert.Null(admission.Rejection);
        Assert.Equal(TimeSpan.Zero, admission.Waited);
    }

    /// <summary>An uncontended caller never enters the queue, so it costs no bookkeeping.</summary>
    [Fact]
    public async Task Uncontended_Admission_NeverEntersTheQueue()
    {
        using var manager = BuildManager();

        await using (var admission = manager.BeginChatInference())
        {
            Assert.Empty(await DrainAsync(admission));
        }

        var snapshot = manager.GetChatQueueSnapshot();
        Assert.Equal(0, snapshot.Depth);
        Assert.Equal(1, snapshot.TotalAdmitted);
        Assert.Equal(0, snapshot.TotalQueuedAdmissions);
        Assert.Equal(0, snapshot.TotalRejections);
        Assert.Equal(0d, snapshot.TotalWaitMilliseconds);
    }

    /// <summary>
    /// Brief contention that resolves before the reporting threshold is still silent: the
    /// user is only told about a wait once the wait is worth telling them about.
    /// </summary>
    [Fact]
    public async Task ContentionShorterThanTheNoticeThreshold_EmitsNoMarkers()
    {
        using var manager = BuildManager(noticeAfterMilliseconds: 600_000);

        var holder = await manager.AcquireChatInferenceAsync();
        await using var admission = manager.BeginChatInference();
        var pump = Task.Run(() => DrainAsync(admission));

        await WaitForAsync(() => manager.ChatQueueDepth == 1, "the second caller to be queued");
        await holder.DisposeAsync();

        Assert.Empty(await pump);
        Assert.True(admission.IsGranted);
    }

    /// <summary>
    /// An already-cancelled token is refused even when the permit is free — the behaviour of
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> that the fast path would
    /// otherwise quietly drop.
    /// </summary>
    [Fact]
    public async Task PreCancelledToken_IsRefused_EvenWhenThePermitIsFree()
    {
        using var manager = BuildManager();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await manager.AcquireChatInferenceAsync(cancelled.Token));

        await AssertPermitIsFreeAsync(manager);
    }

    // ── 2. The wait is bounded ───────────────────────────────────────────────

    /// <summary>
    /// Negative control, documenting exactly what the gate did before this change. The old
    /// body was <c>await gate.WaitAsync(ct)</c>; with the permit held and no deadline on
    /// <c>ct</c> that task is still pending the instant it is created and stays pending for
    /// as long as the holder holds — there is no server-side bound anywhere on the path
    /// (Kestrel applies no request timeout, and the chat token is RequestAborted, which fires
    /// only when the CLIENT gives up).
    /// </summary>
    [Fact]
    public async Task NegativeControl_TheDirectSemaphoreWait_HasNoBoundOfItsOwn()
    {
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        var unbounded = gate.WaitAsync(CancellationToken.None);

        Assert.False(unbounded.IsCompleted);
        gate.Release();
        await unbounded;
    }

    [Fact]
    public async Task WaitLongerThanMaxWait_IsRejected_RatherThanHangingForever()
    {
        using var manager = BuildManager(maxWaitSeconds: 1);

        var holder = await manager.AcquireChatInferenceAsync();

        var rejection = await Assert.ThrowsAsync<InferenceQueueRejectedException>(
            async () => await manager.AcquireChatInferenceAsync());

        Assert.Equal(InferenceQueueRejectionReason.WaitTimeout, rejection.Reason);
        Assert.Equal("chat", rejection.Gate);
        Assert.NotEmpty(rejection.Message);

        // A rejection must not be mistaken for the caller's own cancellation: call sites such
        // as DeepResearchService.TryAcquireChatLeaseAsync catch OperationCanceledException and
        // quietly degrade, which is right for "my budget expired" and wrong for "the server is
        // over capacity". Checked through the type system rather than `is`, because the
        // compiler proves the negative and warns on the direct test.
        Assert.False(
            typeof(OperationCanceledException).IsAssignableFrom(rejection.GetType()),
            "an over-capacity refusal must not masquerade as the caller's own cancellation");

        await holder.DisposeAsync();
        await AssertPermitIsFreeAsync(manager);
    }

    [Fact]
    public async Task QueueDeeperThanMaxQueueDepth_IsRejectedImmediately()
    {
        // Unbounded wait so the ONLY thing that can turn the third caller away is depth.
        using var manager = BuildManager(maxWaitSeconds: 0, maxQueueDepth: 1);

        var holder = await manager.AcquireChatInferenceAsync();
        using var abandon = new CancellationTokenSource();

        var queued = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync(abandon.Token);
        });
        await WaitForAsync(() => manager.ChatQueueDepth == 1, "the first waiter to be queued");

        var elapsed = Stopwatch.StartNew();
        var rejection = await Assert.ThrowsAsync<InferenceQueueRejectedException>(
            async () => await manager.AcquireChatInferenceAsync());
        elapsed.Stop();

        Assert.Equal(InferenceQueueRejectionReason.QueueFull, rejection.Reason);
        Assert.Equal(TimeSpan.Zero, rejection.Waited);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"a full queue must refuse immediately, took {elapsed.ElapsedMilliseconds}ms");

        await abandon.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        await holder.DisposeAsync();
        await AssertPermitIsFreeAsync(manager);
    }

    /// <summary>
    /// The bound is configurable and 0 restores the pre-change unbounded wait exactly, so a
    /// deployment that would rather hang than refuse can still choose to.
    /// </summary>
    [Fact]
    public async Task MaxWaitSecondsZero_RestoresTheUnboundedWait()
    {
        using var manager = BuildManager(maxWaitSeconds: 0);
        Assert.Equal(Timeout.InfiniteTimeSpan, manager.ChatQueueForTests.Options.MaxWait);

        var holder = await manager.AcquireChatInferenceAsync();
        using var giveUp = new CancellationTokenSource();
        var waiter = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync(giveUp.Token);
        });

        await WaitForAsync(() => manager.ChatQueueDepth == 1, "the waiter to be queued");
        Assert.False(waiter.IsCompleted);

        await holder.DisposeAsync();
        await waiter;
    }

    // ── 3. The permit comes back on every exit path ──────────────────────────

    [Fact]
    public async Task Lease_IsReleased_WhenTheCallerFaults()
    {
        using var manager = BuildManager();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync();
            throw new InvalidOperationException("inference blew up");
        });

        await AssertPermitIsFreeAsync(manager);
    }

    [Fact]
    public async Task Lease_IsNotLeaked_WhenTheWaitIsCancelled()
    {
        using var manager = BuildManager(maxWaitSeconds: 0);

        var holder = await manager.AcquireChatInferenceAsync();
        using var cancel = new CancellationTokenSource();

        var waiter = Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await manager.AcquireChatInferenceAsync(cancel.Token));

        await WaitForAsync(() => manager.ChatQueueDepth == 1, "the waiter to be queued");
        await cancel.CancelAsync();
        await waiter;

        await WaitForAsync(() => manager.ChatQueueDepth == 0, "the cancelled waiter to leave the queue");
        await holder.DisposeAsync();
        await AssertPermitIsFreeAsync(manager);
    }

    [Fact]
    public async Task Lease_IsNotLeaked_WhenTheWaitTimesOut()
    {
        using var manager = BuildManager(maxWaitSeconds: 1);

        var holder = await manager.AcquireChatInferenceAsync();
        await Assert.ThrowsAsync<InferenceQueueRejectedException>(
            async () => await manager.AcquireChatInferenceAsync());

        Assert.Equal(0, manager.ChatQueueDepth);
        await holder.DisposeAsync();
        await AssertPermitIsFreeAsync(manager);
    }

    /// <summary>
    /// The nastiest leak: a streaming caller walks away (client disconnect) while parked, and
    /// the semaphore hands the permit to its cancelling node in the same instant. Disposal
    /// must re-await the parked wait and give any such permit back.
    /// </summary>
    [Fact]
    public async Task AbandonedAdmission_GivesThePermitBack()
    {
        using var manager = BuildManager(maxWaitSeconds: 0);

        var holder = await manager.AcquireChatInferenceAsync();
        var admission = manager.BeginChatInference();
        var pump = Task.Run(() => DrainAsync(admission));

        await WaitForAsync(
            () => manager.ChatQueueForTests.EnqueuedWaiterCount == 1,
            "the admission to be parked on the semaphore");

        await admission.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pump);

        Assert.False(admission.IsGranted);
        Assert.Null(admission.Rejection);

        await holder.DisposeAsync();
        await AssertPermitIsFreeAsync(manager);
    }

    /// <summary>
    /// Disposing a granted admission twice must not release the permit twice — a second
    /// Release inflates the semaphore's count and quietly raises the deployment's real
    /// concurrency above SemaphoreLimits:Chat.
    /// </summary>
    [Fact]
    public async Task DisposingAnAdmissionTwice_ReleasesOnce()
    {
        using var manager = BuildManager(chatLimit: 2, maxWaitSeconds: 0);

        var admission = manager.BeginChatInference();
        Assert.Empty(await DrainAsync(admission));
        await admission.DisposeAsync();
        await admission.DisposeAsync();

        await using var first = await manager.AcquireChatInferenceAsync();
        await using var second = await manager.AcquireChatInferenceAsync();

        using var giveUp = new CancellationTokenSource();
        var third = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync(giveUp.Token);
        });

        await WaitForAsync(
            () => manager.ChatQueueDepth == 1,
            "the third caller to queue behind both permits instead of being handed an inflated one");
        Assert.False(third.IsCompleted);

        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await third);
    }

    [Fact]
    public async Task Admission_CannotBeEnumeratedTwice()
    {
        using var manager = BuildManager();

        await using var admission = manager.BeginChatInference();
        Assert.Empty(await DrainAsync(admission));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await DrainAsync(admission));
    }

    // ── 4. Permit count and fairness under contention ────────────────────────

    [Fact]
    public async Task ContendingCallers_NeverExceedThePermitCount()
    {
        const int limit = 3;
        const int callers = 64;
        // Both bounds off: this test is about the permit count, not about admission control.
        using var manager = BuildManager(chatLimit: limit, maxWaitSeconds: 0, maxQueueDepth: 0);

        var concurrent = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync();
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Yield();
            Interlocked.Decrement(ref concurrent);
        })));

        Assert.True(peak <= limit, $"observed {peak} concurrent holders for a limit of {limit}");
        Assert.Equal(0, Volatile.Read(ref concurrent));
        Assert.Equal(0, manager.ChatQueueDepth);
        Assert.Equal(callers, manager.GetChatQueueSnapshot().TotalAdmitted);
    }

    /// <summary>
    /// Sustained contention: every worker must finish every cycle. Starvation shows up here
    /// as a worker that never completes, which fails the test by timing out rather than by a
    /// tuned threshold.
    /// </summary>
    [Fact]
    public async Task SustainedContention_StarvesNobody()
    {
        const int workers = 12;
        const int cyclesPerWorker = 15;
        using var manager = BuildManager(maxWaitSeconds: 0, maxQueueDepth: 0);

        var completed = new int[workers];
        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            for (var cycle = 0; cycle < cyclesPerWorker; cycle++)
            {
                await using var lease = await manager.AcquireChatInferenceAsync();
                completed[worker]++;
                await Task.Yield();
            }
        })));

        Assert.All(completed, count => Assert.Equal(cyclesPerWorker, count));
        Assert.Equal(0, manager.ChatQueueDepth);
        await AssertPermitIsFreeAsync(manager);
    }

    /// <summary>
    /// Fairness. <see cref="SemaphoreSlim"/> documents NO ordering guarantee, so the queue's
    /// "nobody starves" claim rests on an implementation detail: its async waiters form a
    /// FIFO list and <c>Release</c> hands the permit to the head under its own lock (which
    /// is also why a newcomer's <c>Wait(0)</c> fast path cannot barge past a queue). This
    /// test pins that behaviour, so a runtime regression is caught here rather than in
    /// production. Waiters are parked one at a time — the enqueue counter is incremented the
    /// instant a node joins the list — so the intended order is established deterministically.
    /// </summary>
    [Fact]
    public async Task QueuedCallers_AreGrantedInArrivalOrder()
    {
        const int waiters = 6;
        using var manager = BuildManager(maxWaitSeconds: 0);

        var holder = await manager.AcquireChatInferenceAsync();
        var grantOrder = new List<int>();
        var grantLock = new object();
        var tasks = new List<Task>();

        for (var index = 0; index < waiters; index++)
        {
            var arrival = index;
            tasks.Add(Task.Run(async () =>
            {
                await using var lease = await manager.AcquireChatInferenceAsync();
                lock (grantLock) grantOrder.Add(arrival);
                await Task.Yield();
            }));

            var parked = index + 1;
            await WaitForAsync(
                () => manager.ChatQueueForTests.EnqueuedWaiterCount == parked,
                $"waiter {arrival} to park on the semaphore");
        }

        await holder.DisposeAsync();
        await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, waiters).ToList(), grantOrder);
    }

    /// <summary>The reported position matches the arrival order it is derived from.</summary>
    [Fact]
    public async Task QueuePosition_ReflectsArrivalOrder()
    {
        using var manager = BuildManager(maxWaitSeconds: 0, noticeAfterMilliseconds: 1);
        var queue = manager.ChatQueueForTests;

        var holder = await manager.AcquireChatInferenceAsync();

        var admissions = new List<InferenceAdmission>();
        var firstNotices = new List<Task<string>>();
        for (var index = 0; index < 3; index++)
        {
            var admission = manager.BeginChatInference();
            admissions.Add(admission);
            firstNotices.Add(Task.Run(async () =>
            {
                await foreach (var notice in admission.WaitForTurnAsync())
                    return notice;
                return string.Empty;
            }));

            var parked = index + 1;
            await WaitForAsync(() => queue.EnqueuedWaiterCount == parked, $"admission {index} to park");
        }

        for (var index = 0; index < admissions.Count; index++)
        {
            var notice = await firstNotices[index];
            Assert.Contains($"vị trí {index + 1}/", notice, StringComparison.Ordinal);
        }

        foreach (var admission in admissions) await admission.DisposeAsync();
        await holder.DisposeAsync();
    }

    // ── 5. Shutdown ──────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SemaphoreSlim.Dispose"/> does not wake its waiters, so disposing the
    /// manager while callers are queued used to leave them parked on a semaphore that no
    /// longer exists. Shutdown must unblock them, and must not itself block.
    /// </summary>
    [Fact]
    public async Task Shutdown_UnblocksQueuedCallers_AndDoesNotHang()
    {
        var manager = BuildManager(maxWaitSeconds: 0);
        var holder = await manager.AcquireChatInferenceAsync();

        var waiters = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(async () =>
            {
                await using var lease = await manager.AcquireChatInferenceAsync();
            }))
            .ToList();

        await WaitForAsync(
            () => manager.ChatQueueForTests.EnqueuedWaiterCount == 3,
            "all three waiters to park on the semaphore");

        // Bounded rather than "call it and hope": a shutdown that blocks must fail this test
        // with a message, not wedge the test host the way it would wedge the process.
        var disposal = Task.Run(manager.Dispose);
        Assert.Same(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(15))));
        await disposal;

        foreach (var waiter in waiters)
        {
            Assert.Same(waiter, await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(15))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        }

        // Releasing a lease after the gate is gone must not throw out of DisposeAsync.
        var release = holder.DisposeAsync().AsTask();
        Assert.Same(release, await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(15))));
        await release;
    }

    // ── 6. Configuration ─────────────────────────────────────────────────────

    /// <summary>
    /// Pins the shipped defaults. An appsettings block that disagrees with these silently
    /// changes the deployed bounds, so the values documented alongside this change and the
    /// values in code are asserted to be the same numbers.
    /// </summary>
    [Fact]
    public void DefaultsApply_WhenTheInferenceQueueSectionIsAbsent()
    {
        using var manager = new LmModelManager(new ConfigurationBuilder().Build());
        var options = manager.ChatQueueForTests.Options;

        Assert.Equal(32, options.MaxQueueDepth);
        Assert.Equal(300, options.MaxWaitSeconds);
        Assert.Equal(750, options.NoticeAfterMilliseconds);
        Assert.Equal(5, options.NoticeIntervalSeconds);
    }

    [Fact]
    public void ConfiguredValues_OverrideTheDefaults()
    {
        using var manager = BuildManager(
            maxWaitSeconds: 7, maxQueueDepth: 9, noticeAfterMilliseconds: 11, noticeIntervalSeconds: 13);
        var options = manager.ChatQueueForTests.Options;

        Assert.Equal(9, options.MaxQueueDepth);
        Assert.Equal(7, options.MaxWaitSeconds);
        Assert.Equal(11, options.NoticeAfterMilliseconds);
        Assert.Equal(13, options.NoticeIntervalSeconds);
    }

    [Theory]
    [InlineData("MaxQueueDepth", "-1")]
    [InlineData("MaxWaitSeconds", "-1")]
    [InlineData("NoticeAfterMilliseconds", "-1")]
    [InlineData("NoticeIntervalSeconds", "0")]
    public void InvalidQueueConfiguration_FailsFastAtStartup(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"InferenceQueue:Chat:{key}"] = value })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => new LmModelManager(configuration));
        Assert.Contains($"InferenceQueue:Chat:{key}", failure.Message, StringComparison.Ordinal);
    }

    // ── 7. Metrics ───────────────────────────────────────────────────────────

    /// <summary>
    /// The wait, the depth and the refusals reach the meter Program.cs already exports, so an
    /// operator can finally answer "is SemaphoreLimits:Chat = 1 the bottleneck?". The queue
    /// is built directly with a unique gate tag so a parallel test run cannot pollute the
    /// measurements.
    /// </summary>
    [Fact]
    public async Task Metrics_RecordWaitTimeQueueDepthAndRejections()
    {
        var gateName = "chat-metrics-" + Guid.NewGuid().ToString("N");
        using var gate = new SemaphoreSlim(1, 1);
        var queue = new InferenceAdmissionQueue(
            gate,
            gateName,
            new InferenceQueueOptions { MaxWaitSeconds = 1, MaxQueueDepth = 0 },
            NullLogger.Instance);

        var measurements = new List<(string Instrument, double Value, string? Outcome, string? Reason)>();
        var measurementLock = new object();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "LmKitOmniApi.AgentMetrics")
                activeListener.EnableMeasurementEvents(instrument);
        };
        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct
        {
            string? gateTag = null, outcome = null, reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gateTag = tag.Value as string;
                else if (tag.Key == "outcome") outcome = tag.Value as string;
                else if (tag.Key == "reason") reason = tag.Value as string;
            }

            if (gateTag != gateName) return;
            lock (measurementLock)
                measurements.Add((instrument.Name, Convert.ToDouble(value), outcome, reason));
        }

        listener.SetMeasurementEventCallback<double>(Record);
        listener.SetMeasurementEventCallback<long>(Record);
        listener.Start();

        await using (await queue.AcquireAsync())
        {
            var rejection = await Assert.ThrowsAsync<InferenceQueueRejectedException>(
                async () => await queue.AcquireAsync());
            Assert.Equal(InferenceQueueRejectionReason.WaitTimeout, rejection.Reason);
        }

        queue.Dispose();

        List<(string Instrument, double Value, string? Outcome, string? Reason)> observed;
        lock (measurementLock) observed = measurements.ToList();

        Assert.Contains(observed, m => m.Instrument == "inference_queue_admissions_total" && m.Value == 1);
        Assert.Contains(observed, m => m.Instrument == "inference_queue_depth" && m.Value == 1);
        Assert.Contains(observed, m => m.Instrument == "inference_queue_depth" && m.Value == -1);
        Assert.Contains(
            observed,
            m => m.Instrument == "inference_queue_rejections_total" && m.Reason == "WaitTimeout");
        // The recorded wait must be the WHOLE wait (~the 1s bound), not the time up to the
        // point the code started awaiting — the number is useless for capacity planning if it
        // is sampled before the wait happens.
        Assert.Contains(
            observed,
            m => m.Instrument == "inference_queue_wait_ms" && m.Outcome == "rejected" && m.Value >= 900);
        Assert.Contains(
            observed,
            m => m.Instrument == "inference_queue_wait_ms" && m.Outcome == "granted");
    }

    [Fact]
    public async Task Snapshot_CountsQueuedWaitsSeparatelyFromImmediateOnes()
    {
        using var manager = BuildManager(maxWaitSeconds: 0);

        await using (await manager.AcquireChatInferenceAsync())
        {
            // one immediate admission
        }

        var holder = await manager.AcquireChatInferenceAsync();
        var waiter = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireChatInferenceAsync();
        });
        await WaitForAsync(() => manager.ChatQueueDepth == 1, "the waiter to be queued");
        await holder.DisposeAsync();
        await waiter;

        var snapshot = manager.GetChatQueueSnapshot();
        Assert.Equal(3, snapshot.TotalAdmitted);
        Assert.Equal(1, snapshot.TotalQueuedAdmissions);
        Assert.Equal(0, snapshot.TotalRejections);
        Assert.Equal(0, snapshot.Depth);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int current;
        while (candidate > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, candidate, current) == current) return;
        }
    }
}
