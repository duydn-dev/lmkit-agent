using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Services;

/// <summary>
/// Optional startup warm-up for the configured chat model. Model roles are resolved by
/// <see cref="LmModelManager"/> on demand; this service deliberately does not load every
/// role through the chat slot (vision, embedding, speech and reranker have their own loaders).
/// </summary>
public sealed class ModelDownloader : BackgroundService
{
    private readonly LmModelManager _modelManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(
        LmModelManager modelManager,
        IConfiguration configuration,
        ILogger<ModelDownloader> logger)
    {
        _modelManager = modelManager;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the opt-in switch for deployments that want startup warm-up. Catalog IDs are
        // passed to LM-Kit with the configured AiModels:ModelsDirectory as storagePath.
        if (!_configuration.GetValue("AiModels:AutoDownloadModels", false))
        {
            _logger.LogInformation("[ModelDownloader] Startup chat-model warm-up is disabled.");
            return;
        }

        try
        {
            var modelId = _modelManager.DefaultChatModelId;
            _logger.LogInformation("[ModelDownloader] Warming configured chat model {ModelId}.", modelId);
            await _modelManager.GetChatModelAsync(ct: stoppingToken);
            _logger.LogInformation("[ModelDownloader] Chat model {ModelId} is ready.", modelId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            // The first request will retry the load and expose the normal model error path.
            _logger.LogError(ex, "[ModelDownloader] Chat-model warm-up failed; API remains online and will retry on first use.");
        }
    }
}
