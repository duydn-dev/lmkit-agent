using LmKitOmniApi.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LmKitOmniApi.Infrastructure.Health;

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

        if (!_configuration.GetValue("AiModels:RequireChatModelReady", false))
            return Task.FromResult(HealthCheckResult.Healthy("Chat model readiness is not required by configuration."));

        if (_modelManager.IsChatModelLoaded)
            return Task.FromResult(HealthCheckResult.Healthy("Chat model is loaded."));

        var description = _modelManager.LastChatModelLoadError is { Length: > 0 } error
            ? $"Chat model failed to load ({error})."
            : "Chat model has not loaded yet.";
        return Task.FromResult(HealthCheckResult.Unhealthy(description));
    }
}
