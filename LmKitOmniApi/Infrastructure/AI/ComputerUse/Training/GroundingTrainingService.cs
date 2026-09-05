using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Lora;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Default <see cref="IGroundingTrainingService"/>. Pure orchestration/plumbing over the
/// recorder, the LIVE trainer port, and the EXISTING LoRA hot-swap service — no model is
/// loaded here, so it is hermetically CI-testable with a fake port and a fake/real
/// <see cref="ILoraAdapterService"/>.
///
/// Flow: gate on <see cref="GroundingTrainingOptions.Enabled"/> → read the tenant's vetted
/// samples → refuse when below <see cref="GroundingTrainingOptions.MinSamplesToTrain"/> →
/// call the trainer port → register the produced adapter file through
/// <see cref="ILoraAdapterService.RegisterAsync"/> so it becomes hot-swappable into a custom
/// agent / the computer-use loop. Registration also needs the LoRA feature on; when it is
/// off, the adapter is still produced but reported as <see cref="GroundingTrainingStatus.TrainedNotRegistered"/>.
/// </summary>
public sealed class GroundingTrainingService : IGroundingTrainingService
{
    private readonly GroundingTrainingOptions _options;
    private readonly IGroundingTraceRecorder _recorder;
    private readonly IGroundingAdapterTrainerPort _trainer;
    private readonly ILoraAdapterService _loraService;
    private readonly ILogger<GroundingTrainingService> _logger;

    public GroundingTrainingService(
        IOptions<GroundingTrainingOptions> options,
        IGroundingTraceRecorder recorder,
        IGroundingAdapterTrainerPort trainer,
        ILoraAdapterService loraService,
        ILogger<GroundingTrainingService> logger)
    {
        _options = options.Value;
        _recorder = recorder;
        _trainer = trainer;
        _loraService = loraService;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public Task<int> CountSamplesAsync(Guid tenantId, CancellationToken ct = default)
        => _recorder.CountAsync(tenantId, ct);

    public async Task<GroundingTrainingRunResult> TrainAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return GroundingTrainingRunResult.Disabled();

        var samples = await _recorder.ReadAsync(tenantId, ct);
        if (samples.Count < _options.MinSamplesToTrain)
            return GroundingTrainingRunResult.InsufficientSamples(samples.Count, _options.MinSamplesToTrain);

        // Server-controlled, tenant-scoped output path (never a client path).
        var outputDir = Path.Combine(_options.ResolveAdapterOutputRoot(), tenantId.ToString("N"));
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var adapterPath = Path.Combine(outputDir, $"grounding-{stamp}-{Guid.NewGuid():N}.gguf");

        // ── Train (LIVE seam) ──
        GroundingTrainResult trained;
        try
        {
            Directory.CreateDirectory(outputDir);
            trained = await _trainer.TrainAsync(samples, _options, adapterPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grounding LoRA training failed for tenant {TenantId}.", tenantId);
            return GroundingTrainingRunResult.Failed("Training failed: " + ex.Message);
        }

        // ── Register the produced adapter via the EXISTING LoRA hot-swap feature ──
        // (read the file back as a stream and hand it to RegisterAsync, exactly like an
        // Admin upload, so the grounding adapter becomes hot-swappable).
        try
        {
            await using var content = File.OpenRead(trained.AdapterPath);
            var name = $"grounding-{stamp}-{Guid.NewGuid().ToString("N")[..8]}";
            var registration = await _loraService.RegisterAsync(
                tenantId,
                name,
                $"Auto-trained computer-use grounding adapter ({trained.SampleCount} vetted samples).",
                content,
                content.Length,
                scale: null,
                targetModelId: null,
                ct);

            _logger.LogInformation(
                "Registered grounding LoRA adapter {AdapterId} for tenant {TenantId} ({Samples} samples).",
                registration.Id, tenantId, trained.SampleCount);
            return GroundingTrainingRunResult.Trained(registration.Id, trained.AdapterPath, trained.SampleCount);
        }
        catch (LoraFeatureDisabledException)
        {
            _logger.LogWarning(
                "Grounding adapter trained for tenant {TenantId} but the LoRA hot-swap feature (Lora:Enabled) is off; not registered.",
                tenantId);
            return GroundingTrainingRunResult.TrainedNotRegistered(
                trained.AdapterPath, trained.SampleCount,
                "Adapter trained but not registered: enable the LoRA hot-swap feature (Lora:Enabled) to make it hot-swappable.");
        }
        catch (LoraAdapterValidationException ex)
        {
            _logger.LogWarning(ex, "Grounding adapter trained but failed LoRA registration validation for tenant {TenantId}.", tenantId);
            return GroundingTrainingRunResult.TrainedNotRegistered(
                trained.AdapterPath, trained.SampleCount, "Adapter trained but registration was rejected: " + ex.Message);
        }
    }
}
