using LmKitOmniApi.Services;

namespace LmKitOmniApi.Infrastructure.Workers;

public sealed class ModelWarmupWorker : BackgroundService
{
    private readonly LmModelManager _modelManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelWarmupWorker> _logger;

    public ModelWarmupWorker(
        LmModelManager modelManager,
        IConfiguration configuration,
        ILogger<ModelWarmupWorker> logger)
    {
        _modelManager = modelManager;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("AiModels:WarmupChatModel", false)) return;

        try
        {
            _logger.LogInformation("Warming up the configured LM-Kit chat model.");
            await _modelManager.GetChatModelAsync(ct: stoppingToken);
            _logger.LogInformation("LM-Kit chat model warmup completed.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM-Kit chat model warmup failed; readiness will remain unhealthy when required.");
        }
    }
}
