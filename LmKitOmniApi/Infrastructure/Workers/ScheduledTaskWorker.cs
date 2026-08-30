using LMKit.Model;
using LMKit.TextGeneration;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.Workers;

/// <summary>
/// Runs user-defined scheduled prompts (<see cref="ScheduledTask"/>) and delivers results as
/// <see cref="Notification"/> rows. Follows the house worker pattern
/// (<see cref="DocumentVectorizationWorker"/>): scope per iteration, atomic per-row lease claim
/// via <c>ExecuteUpdateAsync</c>, and layered try/catch so the worker never crashes the host.
/// </summary>
public class ScheduledTaskWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Hard cap for one completion, kept safely below <see cref="LeaseDuration"/> so a hung
    /// inference cannot outlive its lease and be double-claimed by another replica.
    /// </summary>
    private static readonly TimeSpan MaxRunDuration = TimeSpan.FromMinutes(8);

    private const int MaxTasksPerIteration = 5;
    private const int CompletionTokenLimit = 1024;
    private const int MaxNotificationBodyLength = 4000;
    private const int MaxErrorLength = 500;

    private const string SucceededStatus = "Succeeded";
    private const string FailedStatus = "Failed";
    private const string SkippedStatus = "Skipped";

    private const string ResultNotificationType = "scheduled";
    private const string ErrorNotificationType = "scheduled_error";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScheduledTaskWorker> _logger;

    public ScheduledTaskWorker(IServiceProvider serviceProvider, ILogger<ScheduledTaskWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Job: Scheduled Task Worker is starting.");
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
                var modelManager = scope.ServiceProvider.GetRequiredService<LmModelManager>();

                var now = DateTime.UtcNow;
                var candidateIds = await dbContext.ScheduledTasks
                    .Where(task => task.Enabled
                        && task.NextRunUtc <= now
                        && (task.ClaimedUntilUtc == null || task.ClaimedUntilUtc < now))
                    .OrderBy(task => task.NextRunUtc)
                    .Select(task => task.Id)
                    .Take(MaxTasksPerIteration)
                    .ToListAsync(stoppingToken);

                if (candidateIds.Count == 0) continue;

                foreach (var taskId in candidateIds)
                {
                    try
                    {
                        // Atomic lease claim: the conditional UPDATE only succeeds for a row that
                        // is still due and unclaimed, so concurrent replicas cannot run the same
                        // task twice within a lease window.
                        var leaseUntil = DateTime.UtcNow.Add(LeaseDuration);
                        var claimed = await dbContext.ScheduledTasks
                            .Where(task => task.Id == taskId
                                && task.Enabled
                                && task.NextRunUtc <= DateTime.UtcNow
                                && (task.ClaimedUntilUtc == null || task.ClaimedUntilUtc < DateTime.UtcNow))
                            .ExecuteUpdateAsync(update => update
                                .SetProperty(task => task.ClaimedUntilUtc, leaseUntil), stoppingToken);
                        if (claimed != 1) continue;

                        var task = await dbContext.ScheduledTasks.SingleAsync(candidate => candidate.Id == taskId, stoppingToken);
                        await RunClaimedTaskAsync(dbContext, modelManager, task, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Per-task guard: one bad task (e.g. a SaveChanges conflict after the row
                        // was deleted mid-run) must not abort the rest of the batch. The lease
                        // simply expires and the task is retried on a later tick.
                        _logger.LogError(ex, "Unexpected error while running scheduled task {TaskId}", taskId);
                    }
                    finally
                    {
                        // Each task's changes are persisted inside RunClaimedTaskAsync; drop any
                        // leftover tracked state so a failed task cannot poison the next one.
                        dbContext.ChangeTracker.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in scheduled task worker iteration");
            }
        }
    }

    private async Task RunClaimedTaskAsync(
        HermesDbContext dbContext,
        LmModelManager modelManager,
        ScheduledTask task,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTime.UtcNow;
        var (status, error, notification) = await ExecuteTaskAsync(modelManager, task, stoppingToken);

        // ALWAYS finalize the run bookkeeping, whatever the outcome above.
        task.LastRunUtc = runStartedUtc;
        task.LastStatus = status;
        task.LastError = error;
        task.ClaimedUntilUtc = null;
        try
        {
            task.NextRunUtc = ScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow);
        }
        catch (InvalidOperationException scheduleError)
        {
            // Corrupt schedule definition (cannot happen through the API): disable instead of
            // hot-looping the row every tick with a stale NextRunUtc.
            task.Enabled = false;
            task.LastStatus = FailedStatus;
            task.LastError = Truncate($"Invalid schedule definition: {scheduleError.Message}", MaxErrorLength);
            _logger.LogError(scheduleError, "Disabling scheduled task {TaskId}: schedule definition is invalid", task.Id);
        }

        if (notification is not null)
            dbContext.Notifications.Add(notification);

        await dbContext.SaveChangesAsync(stoppingToken);
        _logger.LogInformation("Scheduled task {TaskId} ({TaskName}) finished with status {Status}",
            task.Id, task.Name, task.LastStatus);
    }

    /// <summary>
    /// Runs the task prompt and maps the outcome to (LastStatus, LastError, notification).
    /// Model unavailable → Skipped with no notification (a warning log only, to avoid
    /// notification spam while the model or license is down); any other failure → Failed with
    /// a short Vietnamese error notification.
    /// </summary>
    private async Task<(string Status, string? Error, Notification? Notification)> ExecuteTaskAsync(
        LmModelManager modelManager,
        ScheduledTask task,
        CancellationToken stoppingToken)
    {
        LM model;
        try
        {
            model = await modelManager.GetChatModelAsync(ct: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled task {TaskId} skipped: chat model is unavailable", task.Id);
            return (SkippedStatus, Truncate($"Chat model unavailable: {ex.GetType().Name}", MaxErrorLength), null);
        }

        try
        {
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            runCts.CancelAfter(MaxRunDuration);

            string completion;
            await using (var inferenceLease = await modelManager.AcquireChatInferenceAsync(runCts.Token))
            {
                completion = await RunSingleTurnCompletionAsync(model, task.Prompt, runCts.Token);
            }

            var body = string.IsNullOrWhiteSpace(completion)
                ? "(Mô hình không trả về nội dung.)"
                : Truncate(completion.Trim(), MaxNotificationBodyLength);

            var notification = new Notification
            {
                TenantId = task.TenantId,
                UserId = task.UserId,
                Type = ResultNotificationType,
                Title = task.Name,
                Body = body
            };
            return (SucceededStatus, null, notification);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The linked timeout fired, not the host shutdown: record a failure.
            _logger.LogWarning("Scheduled task {TaskId} timed out after {Timeout}", task.Id, MaxRunDuration);
            return (FailedStatus,
                Truncate($"Task run exceeded the {MaxRunDuration.TotalMinutes:0} minute limit.", MaxErrorLength),
                BuildErrorNotification(task));
        }
        catch (Exception ex) when (ex is LMKit.Exceptions.LicenseException
            or LMKit.Exceptions.ModelNotLoadedException
            or LMKit.Exceptions.ModelNotDownloadedException
            or LMKit.Exceptions.InvalidModelException)
        {
            // License disabled or model gone at inference time: skip without notification spam.
            _logger.LogWarning(ex, "Scheduled task {TaskId} skipped: model/license unavailable", task.Id);
            return (SkippedStatus, Truncate($"Model unavailable: {ex.GetType().Name}", MaxErrorLength), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task {TaskId} failed during completion", task.Id);
            return (FailedStatus, Truncate(ex.Message, MaxErrorLength), BuildErrorNotification(task));
        }
    }

    /// <summary>
    /// Single-turn completion on a dedicated thread. <c>SingleTurnConversation.Submit</c> is a
    /// blocking call that holds a thread for the entire inference, so — mirroring the
    /// orchestrator's C1 pattern — it must not run on the ThreadPool where it could contribute
    /// to pool starvation. The cancellation token is passed into Submit (LMKit honors it) and
    /// also guards the await so a stuck inference is abandoned rather than awaited forever.
    /// </summary>
    private static async Task<string> RunSingleTurnCompletionAsync(LM model, string prompt, CancellationToken ct)
    {
        var completionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmThread = new Thread(() =>
        {
            try
            {
                var chat = new SingleTurnConversation(model)
                {
                    MaximumCompletionTokens = CompletionTokenLimit
                };
                var result = chat.Submit(prompt, ct);
                completionSource.TrySetResult(result.Completion ?? string.Empty);
            }
            catch (OperationCanceledException)
            {
                completionSource.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"Sched-LLM-{Guid.NewGuid():N}"
        };
        llmThread.Start();

        return await completionSource.Task.WaitAsync(ct);
    }

    private static Notification BuildErrorNotification(ScheduledTask task) => new()
    {
        TenantId = task.TenantId,
        UserId = task.UserId,
        Type = ErrorNotificationType,
        Title = task.Name,
        Body = "Lịch tự động gặp lỗi khi thực thi. Vui lòng kiểm tra lại nội dung nhắc lệnh hoặc thử lại sau."
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
