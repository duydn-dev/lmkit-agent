using LmKitOmniApi.Services;
using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// Builds the <see cref="ChatHistory"/> a continuation's synthesis pass runs against.
///
/// <para>A seam, not indirection for its own sake: <see cref="ChatHistory"/> is
/// constructed from the loaded chat model, so without it every test of the resume
/// lifecycle would need multi-gigabyte weights on disk. The production implementation is
/// the same two lines <c>StreamAgentRunCommandHandler</c> uses to start a run.</para>
/// </summary>
public interface IAgentRunHistoryFactory
{
    Task<ChatHistory> CreateAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class LmKitAgentRunHistoryFactory : IAgentRunHistoryFactory
{
    private readonly LmModelManager _modelManager;

    public LmKitAgentRunHistoryFactory(LmModelManager modelManager) => _modelManager = modelManager;

    public async Task<ChatHistory> CreateAsync(CancellationToken ct)
        => new(await _modelManager.GetChatModelAsync(ct: ct));
}

/// <summary>
/// Runs <see cref="AgentRunResumeService"/> off a poll, plus a direct wake-up from the
/// approve request on the same replica (<see cref="AgentRunResumeQueue"/>).
///
/// <para>Modelled on <c>ApprovalExpiryWorker</c>: one pass at startup — which is also the
/// crash-recovery path, since a continuation whose process died is exactly a claim with a
/// lapsed lease — then one per tick, each in its own scope, with every failure logged and
/// swallowed so a transient database problem cannot kill the worker for the lifetime of
/// the process.</para>
///
/// <para><b>Disabled is a DRAIN, not a stand-down.</b> Anything queued while the feature
/// was on is closed at the truthful <c>CompletedAfterApproval</c> before this worker
/// stops. Standing down instead would leave those runs marked for a continuation nobody
/// will ever give them — reintroducing, by configuration change, precisely the
/// "parked forever" state this work exists to remove.</para>
/// </summary>
public sealed class AgentRunResumeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunResumeQueue _queue;
    private readonly AgentRunResumeOptions _options;
    private readonly ILogger<AgentRunResumeWorker> _logger;

    public AgentRunResumeWorker(
        IServiceScopeFactory scopeFactory,
        AgentRunResumeQueue queue,
        ILogger<AgentRunResumeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = queue.Options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Agent-run resume is disabled; an approved gated call is recorded and the run ends at "
                + "CompletedAfterApproval. Draining anything queued while it was enabled.");
            await RunPassAsync(drainOnly: true, stoppingToken);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassAsync(drainOnly: false, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
            await _queue.WaitAsync(_options.PollInterval, stoppingToken);
        }
    }

    private async Task RunPassAsync(bool drainOnly, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<AgentRunResumeService>();
            if (drainOnly) await service.DrainAsync(ct);
            else await service.ResumePendingAsync(DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure. A pass cut short here left its run re-queued.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent-run resume pass failed; retrying on the next tick.");
        }
    }
}

/// <summary>Single-call registration for resuming an agent run after an approval.</summary>
public static class AgentRunResumeServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="AgentRunResumeOptions"/> from the "AgentRunResume" section and
    /// registers the queue, the service and its hosted worker.
    ///
    /// <para><b>Omitting this call is safe and means "no resume".</b>
    /// <c>ApproveTaskCommandHandler</c> takes <see cref="AgentRunResumeQueue"/> as an
    /// optional dependency defaulting to <c>null</c>, and only a non-null queue makes the
    /// reconciler mark a run for continuation — so a host that never calls this (an
    /// integration-test host, a deployment that opts out) behaves exactly as it did
    /// before the feature existed rather than queuing work nobody will drive.</para>
    /// </summary>
    public static IServiceCollection AddAgentRunResume(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentRunResumeOptions>(configuration.GetSection(AgentRunResumeOptions.SectionName));
        services.AddSingleton<AgentRunResumeQueue>();
        services.AddSingleton<IAgentRunHistoryFactory, LmKitAgentRunHistoryFactory>();
        services.AddScoped<AgentRunResumeService>();
        services.AddHostedService<AgentRunResumeWorker>();
        return services;
    }
}
