using LmKitOmniApi.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LmKitOmniApi.Infrastructure.Health;

/// <summary>
/// Readiness for the LM-Kit side of the app.
///
/// The default configuration uses an LM-Kit catalog ID. The model is downloaded/reused by
/// LM-Kit at load time using AiModels:ModelsDirectory as storagePath, so this check must not
/// pretend a catalog ID is a local filename. With WarmupChatModel=false and
/// RequireChatModelReady=false, startup can be healthy before the first model load.
/// </summary>
public sealed class LmKitModelHealthCheck : IHealthCheck
{
    private readonly LmModelManager _modelManager;
    private readonly IConfiguration _configuration;

    public LmKitModelHealthCheck(LmModelManager modelManager, IConfiguration configuration)
    {
        _modelManager = modelManager;
        _configuration = configuration;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_configuration.GetValue("LMKit:RequireLicense", false)
            && string.IsNullOrWhiteSpace(_configuration["LMKit:LicenseKey"]))
            return Task.FromResult(HealthCheckResult.Unhealthy("Required LM-Kit license is not configured."));

        // Unresolvable/failed chat model → unhealthy even when readiness is "optional".
        // The description names the config key and the missing path so the operator can act
        // on it; the default health response writer emits only the status word, so nothing
        // leaks over the wire.
        if (_modelManager.DescribeDefaultChatModelProblem() is { Length: > 0 } problem)
            return Task.FromResult(HealthCheckResult.Unhealthy(problem));

        if (!_configuration.GetValue("AiModels:RequireChatModelReady", false))
            return Task.FromResult(HealthCheckResult.Healthy("Chat model readiness is not required by configuration."));

        if (_modelManager.IsChatModelLoaded)
            return Task.FromResult(HealthCheckResult.Healthy("Chat model is loaded."));

        return Task.FromResult(HealthCheckResult.Unhealthy("Chat model has not loaded yet."));
    }
}
