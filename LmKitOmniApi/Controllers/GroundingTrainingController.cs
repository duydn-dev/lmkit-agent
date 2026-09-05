using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Admin/consumer surface for the computer-use <b>grounding fine-tuning</b> pipeline
/// (<c>api/computer-use/grounding-training</c>): read how many vetted samples have been
/// captured for the tenant, and (Admin-only) kick off an offline LoRA training run that
/// registers the produced adapter through the existing LoRA hot-swap feature.
///
/// Tenant-scoped from the JWT claims (never the body). OFF BY DEFAULT — every endpoint
/// returns <b>501</b> while <c>GroundingTraining:Enabled</c> is false. This controller and
/// its services compile and are unit-tested WITHOUT touching Program.cs / appsettings.json /
/// ComputerUseAgent.cs / AgentOrchestrator.cs (the DI + config + recorder-hook wiring is
/// documented in TRAINING-INTEGRATION.md).
/// </summary>
[ApiController]
[Route("api/computer-use/grounding-training")]
[Authorize]
public sealed class GroundingTrainingController : ApiControllerBase
{
    private readonly IGroundingTrainingService _service;

    public GroundingTrainingController(IGroundingTrainingService service) => _service = service;

    /// <summary>Vetted-sample count for the caller's tenant. 501 when the feature is off.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        if (!_service.Enabled) return FeatureOff();

        var count = await _service.CountSamplesAsync(tenantId, ct);
        return Ok(new { enabled = true, sampleCount = count });
    }

    /// <summary>
    /// Kicks off an offline grounding-adapter training run for the caller's tenant and
    /// registers the produced adapter via the LoRA hot-swap feature. Admin-only. 501 when the
    /// feature is off, 409 when there are not enough vetted samples yet, 500 when training
    /// fails.
    /// </summary>
    [HttpPost("run")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        if (!_service.Enabled) return FeatureOff();

        var result = await _service.TrainAsync(tenantId, ct);
        return result.Status switch
        {
            GroundingTrainingStatus.Disabled => FeatureOff(),
            GroundingTrainingStatus.InsufficientSamples => Conflict(new
            {
                message = result.Message,
                sampleCount = result.SampleCount,
                required = result.RequiredSamples,
            }),
            GroundingTrainingStatus.Failed => StatusCode(
                StatusCodes.Status500InternalServerError, new { message = result.Message }),
            GroundingTrainingStatus.TrainedNotRegistered => Ok(new
            {
                registered = false,
                message = result.Message,
                adapterPath = result.AdapterPath,
                sampleCount = result.SampleCount,
            }),
            _ => Ok(new
            {
                registered = true,
                adapterId = result.AdapterId,
                adapterPath = result.AdapterPath,
                sampleCount = result.SampleCount,
            }),
        };
    }

    private IActionResult FeatureOff() =>
        StatusCode(StatusCodes.Status501NotImplemented,
            new { message = "Tính năng huấn luyện grounding chưa được bật." });
}
