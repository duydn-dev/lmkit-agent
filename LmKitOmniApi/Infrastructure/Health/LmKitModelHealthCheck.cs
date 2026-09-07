using LmKitOmniApi.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LmKitOmniApi.Infrastructure.Health;

/// <summary>
/// Readiness for the LM-Kit side of the app.
///
/// The gap this closes: <c>AiModels:DefaultChat</c> can name a registered model whose weights
/// file simply is not on disk. With <c>WarmupChatModel=false</c> and
/// <c>RequireChatModelReady=false</c> — the shipped defaults — startup succeeded and
/// <c>/health/ready</c> reported healthy, so an orchestrator happily routed traffic to an
/// instance that could not answer a single chat message. The failure only surfaced on the
/// first user turn.
///
/// Now a chat model that CANNOT BE RESOLVED is unhealthy regardless of the
/// <c>RequireChatModelReady</c> switch: that flag governs whether the model must already be
/// LOADED (a warmup concern), not whether the deployment is coherent. Liveness
/// (<c>/health</c>, <c>/health/live</c>) is untouched — the process is alive either way, and
/// restarting it would not conjure a missing file.
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
