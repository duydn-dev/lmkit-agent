using LMKit.Model;
using LMKit.TextGeneration;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Application.Schedules;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using MediatR;
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
    private const string AwaitingApprovalStatus = "AwaitingApproval";

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
                        await RunClaimedTaskAsync(scope.ServiceProvider, dbContext, modelManager, task, stoppingToken);
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
        IServiceProvider scopedServices,
        HermesDbContext dbContext,
        LmModelManager modelManager,
        ScheduledTask task,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTime.UtcNow;
        var (status, error, notification) = task.RunMode == ScheduledTaskRules.AgentRunMode
            ? await ExecuteAgentTaskAsync(scopedServices, modelManager, task, stoppingToken)
            : await ExecuteTaskAsync(scopedServices, modelManager, task, stoppingToken);

        // Giao webhook (nếu lịch khai): CHỈ khi run thành công, và thất bại giao không
        // bao giờ đổi kết quả run — chỉ nối cảnh báo vào notification + log.
        if (status == SucceededStatus && notification is not null
            && !string.IsNullOrWhiteSpace(task.DeliveryWebhookUrl))
        {
            var deliverer = scopedServices.GetRequiredService<LmKitOmniApi.Infrastructure.AI.Web.ScheduleWebhookDeliverer>();
            string? deliveryError;
            try
            {
                deliveryError = await deliverer.DeliverAsync(
                    task.Id, task.Name, task.RunMode, notification.Body, DateTime.UtcNow,
                    task.DeliveryWebhookUrl!, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "📮 [ScheduleWebhook] Task {TaskId}: lỗi không lường trước khi giao.", task.Id);
                deliveryError = "lỗi không xác định khi gửi";
            }
            if (deliveryError is not null)
            {
                notification.Body = Truncate(
                    notification.Body + $"\n\n⚠️ Giao webhook thất bại: {deliveryError}.", MaxNotificationBodyLength);
            }
        }

        // ALWAYS finalize the run bookkeeping, whatever the outcome above.
        task.LastRunUtc = runStartedUtc;
        task.LastStatus = status;
        task.LastError = error;
        task.ClaimedUntilUtc = null;
        try
        {
            // Toàn bộ sổ sách sau-run (kể cả kind "once" tự tắt sau phát duy nhất và
            // Skipped thử lại ~10 phút) nằm trong ScheduledTaskRules.AdvanceAfterRun
            // — thuần logic, test được không cần worker.
            ScheduledTaskRules.AdvanceAfterRun(task, status, DateTime.UtcNow);
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
    /// Chế độ "agent": chạy prompt qua ĐÚNG pipeline AgentRun (StreamAgentRunCommand) —
    /// phiên ẩn + hàng AgentRun + từng bước tool được lưu, HITL giữ nguyên. Nhờ vậy lịch
    /// tự động dùng được mọi tool đọc (CSDL đã index, tri thức, web…), còn tool cần phê
    /// duyệt chỉ tạm dừng phiên chờ người dùng như một run thủ công. Kết quả cuối được
    /// đọc lại từ hàng AgentRun (đã strip marker) và giao qua Notification.
    /// Model/hàng đợi bận → Skipped (thử lại sớm), giống nhánh completion.
    /// </summary>
    private async Task<(string Status, string? Error, Notification? Notification)> ExecuteAgentTaskAsync(
        IServiceProvider scopedServices,
        LmModelManager modelManager,
        ScheduledTask task,
        CancellationToken stoppingToken)
    {
        // Pre-flight giống nhánh completion: model chưa sẵn sàng → Skipped, không spam lỗi.
        try
        {
            _ = await modelManager.GetChatModelAsync(ct: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled agent task {TaskId} skipped: chat model is unavailable", task.Id);
            return (SkippedStatus, Truncate($"Chat model unavailable: {ex.GetType().Name}", MaxErrorLength), null);
        }

        var mediator = scopedServices.GetRequiredService<IMediator>();
        var command = new StreamAgentRunCommand
        {
            TenantId = task.TenantId,
            UserId = task.UserId,
            Goal = task.Prompt,
            CustomAgentId = task.CustomAgentId
        };

        try
        {
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            runCts.CancelAfter(MaxRunDuration);

            // Drain the stream — the handler persists session/run/steps itself; the text
            // stream here is only progress markers we do not need to retain.
            await foreach (var _ in mediator.CreateStream(command, runCts.Token))
            {
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InferenceQueueRejectedException refused)
        {
            // Máy đang kín chỗ suy luận: run đã được handler ghi Failed, nhưng với LỊCH thì
            // đây là tình huống tạm thời — Skipped để thử lại trong ~10 phút, không spam lỗi.
            _logger.LogWarning("Scheduled agent task {TaskId} skipped: {Reason}", task.Id, refused.Message);
            return (SkippedStatus, Truncate(refused.Message, MaxErrorLength), null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Scheduled agent task {TaskId} timed out after {Timeout}", task.Id, MaxRunDuration);
            return (FailedStatus,
                Truncate($"Task run exceeded the {MaxRunDuration.TotalMinutes:0} minute limit.", MaxErrorLength),
                BuildErrorNotification(task));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled agent task {TaskId} failed", task.Id);
            return (FailedStatus, Truncate(ex.Message, MaxErrorLength), BuildErrorNotification(task));
        }

        // Đọc kết quả thật từ hàng AgentRun bằng một scope DbContext MỚI: dbContext của
        // vòng lặp còn đang track ScheduledTask, và handler đã lưu bằng context của scope này.
        using var runReader = scopedServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var runDb = runReader.ServiceProvider.GetRequiredService<HermesDbContext>();
        var run = await runDb.AgentRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == command.RunId, stoppingToken);

        if (run is null)
            return (FailedStatus, "Agent run row was not persisted.", BuildErrorNotification(task));

        if (run.Status == AgentRunStatuses.AwaitingApproval)
        {
            var pending = new Notification
            {
                TenantId = task.TenantId,
                UserId = task.UserId,
                Type = ResultNotificationType,
                Title = task.Name,
                Body = "Lịch tự động đã chạy đến bước cần phê duyệt. Vào màn Phê duyệt tác vụ để xem xét — phiên sẽ tự chạy tiếp sau khi bạn quyết định."
            };
            return (AwaitingApprovalStatus, null, pending);
        }

        if (run.Status == AgentRunStatuses.Completed)
        {
            var body = string.IsNullOrWhiteSpace(run.Result)
                ? "(Agent không trả về nội dung.)"
                : Truncate(run.Result.Trim(), MaxNotificationBodyLength);
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

        return (FailedStatus,
            Truncate(run.Error ?? "Agent run failed.", MaxErrorLength),
            BuildErrorNotification(task));
    }

    /// <summary>
    /// Runs the task prompt and maps the outcome to (LastStatus, LastError, notification).
    /// Model unavailable → Skipped with no notification (a warning log only, to avoid
    /// notification spam while the model or license is down); any other failure → Failed with
    /// a short Vietnamese error notification.
    /// </summary>
    private async Task<(string Status, string? Error, Notification? Notification)> ExecuteTaskAsync(
        IServiceProvider scopedServices,
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

            // Persona (tùy chọn) cho chế độ completion: SystemPrompt của lượt suy luận.
            // DbContext RIÊNG vì context của vòng lặp đang track ScheduledTask.
            string? personaPrompt = null;
            if (task.CustomAgentId is { } personaAgentId)
            {
                using var personaScope = scopedServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var personaDb = personaScope.ServiceProvider.GetRequiredService<HermesDbContext>();
                personaPrompt = await personaDb.CustomAgents.AsNoTracking()
                    .Where(agent => agent.Id == personaAgentId
                        && agent.TenantId == task.TenantId
                        && (agent.OwnerUserId == task.UserId || agent.IsSharedWithTenant))
                    .Select(agent => agent.PersonaPrompt)
                    .FirstOrDefaultAsync(runCts.Token);
                if (personaPrompt is null)
                {
                    _logger.LogWarning(
                        "Scheduled task {TaskId}: persona agent {AgentId} không còn truy cập được — chạy không persona.",
                        task.Id, personaAgentId);
                }
            }

            string completion;
            await using (var inferenceLease = await modelManager.AcquireChatInferenceAsync(runCts.Token))
            {
                var (resultTask, threadCompleted) = StartSingleTurnCompletion(model, task.Prompt, personaPrompt, runCts.Token);
                try
                {
                    completion = await resultTask.WaitAsync(runCts.Token);
                }
                finally
                {
                    // Hold the single-slot inference lease until the dedicated thread has fully
                    // unwound out of the native Submit call. On the timeout path WaitAsync above
                    // abandons resultTask while Submit is still running on the shared model, so
                    // releasing the lease now would let a concurrent inference start. The run CTS
                    // has already signalled Submit to cancel cooperatively — here we only wait for
                    // it to return. Any fault on the completion signal is irrelevant to the join.
                    try { await threadCompleted; } catch { /* best-effort join before release */ }
                }
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
    /// Starts a single-turn completion on a dedicated thread and returns two tasks: the completion
    /// <c>Result</c>, and a <c>Completed</c> signal that fires once the thread has unwound out of
    /// the native call. <c>SingleTurnConversation.Submit</c> is a blocking call that holds a thread
    /// for the entire inference, so — mirroring the orchestrator's C1 pattern — it must not run on
    /// the ThreadPool where it could contribute to pool starvation. The cancellation token is
    /// passed into Submit (LMKit honors it cooperatively). The caller awaits <c>Result</c> with a
    /// timeout, but MUST await <c>Completed</c> before releasing the inference lease so a timed-out
    /// run cannot leave native inference running on the shared model past lease release.
    /// </summary>
    private static (Task<string> Result, Task Completed) StartSingleTurnCompletion(
        LM model, string prompt, string? systemPrompt, CancellationToken ct)
    {
        var completionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmThread = new Thread(() =>
        {
            try
            {
                var chat = new SingleTurnConversation(model)
                {
                    MaximumCompletionTokens = CompletionTokenLimit
                };
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    chat.SystemPrompt = systemPrompt;
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
            finally
            {
                // Fires once Submit has returned or observed cancellation — i.e. the thread is no
                // longer inside native inference. The caller awaits this before releasing the
                // inference lease, so the single-slot gate is never freed while Submit is still
                // running on the shared model.
                threadCompleted.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = $"Sched-LLM-{Guid.NewGuid():N}"
        };
        llmThread.Start();

        return (completionSource.Task, threadCompleted.Task);
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
