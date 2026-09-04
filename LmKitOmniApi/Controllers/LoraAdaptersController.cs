using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Application.LoraAdapters.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// LoRA hot-swap admin/consumer surface (<c>api/lora-adapters</c>). Adapter files are
/// Admin-uploaded ONLY (POST/DELETE are Admin-scoped); listing/reading and assigning an
/// adapter to one of the caller's own custom agents are available to any authenticated
/// user, tenant-scoped from the JWT claims (never the body). The whole feature is OFF BY
/// DEFAULT — every mutation returns 501 while <c>Lora:Enabled</c> is false; GET returns an
/// empty list.
/// </summary>
[ApiController]
[Route("api/lora-adapters")]
[Authorize]
public sealed class LoraAdaptersController : ApiControllerBase
{
    // Largest adapter upload we let model binding buffer before the service's authoritative
    // streaming cap runs — a coarse first gate matching LoraOptions' 512 MB default.
    private const long MaxUploadBytes = 512L * 1024 * 1024;

    private readonly IMediator _mediator;

    public LoraAdaptersController(IMediator mediator) => _mediator = mediator;

    /// <summary>All adapters for the caller's tenant. Empty when the feature is off.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var adapters = await _mediator.Send(new GetLoraAdaptersQuery { TenantId = tenantId }, ct);
        return Ok(adapters);
    }

    /// <summary>One adapter by id, tenant-scoped. 404 when missing.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var adapter = await _mediator.Send(new GetLoraAdapterQuery { TenantId = tenantId, Id = id }, ct);
        return adapter is null ? NotFound() : Ok(adapter);
    }

    /// <summary>
    /// Uploads and registers a new adapter (multipart form: <c>file</c> plus name /
    /// description / scale / targetModelId). Admin-only. 501 when the feature is off.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        [FromForm] string name,
        [FromForm] string? description,
        [FromForm] float? scale,
        [FromForm] string? targetModelId,
        IFormFile? file,
        CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        if (file is null || file.Length == 0) return BadRequest(new { message = "Chưa có tệp adapter nào được tải lên." });
        if (file.Length > MaxUploadBytes) return BadRequest(new { message = $"Tệp adapter vượt quá giới hạn {MaxUploadBytes} byte." });

        await using var content = file.OpenReadStream();
        var result = await _mediator.Send(new RegisterLoraAdapterCommand
        {
            TenantId = tenantId,
            Name = name,
            Description = description,
            Content = content,
            ContentLength = file.Length,
            Scale = scale,
            TargetModelId = targetModelId
        }, ct);

        return result.Status switch
        {
            LoraMutationStatus.FeatureDisabled => FeatureOff(),
            LoraMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => CreatedAtAction(nameof(Get), new { id = result.Adapter!.Id }, result.Adapter)
        };
    }

    /// <summary>Updates name / scale / active. Admin-only. 501 when off, 404 when missing.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLoraAdapterRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateLoraAdapterCommand
        {
            TenantId = tenantId,
            Id = id,
            Name = request.Name,
            Scale = request.Scale,
            IsActive = request.IsActive
        }, ct);

        return result.Status switch
        {
            LoraMutationStatus.FeatureDisabled => FeatureOff(),
            LoraMutationStatus.NotFound => NotFound(),
            LoraMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => Ok(result.Adapter)
        };
    }

    /// <summary>Deletes an adapter (row + file). Admin-only. 501 when off, 404 when missing.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new DeleteLoraAdapterCommand { TenantId = tenantId, Id = id }, ct);
        return result.Status switch
        {
            LoraMutationStatus.FeatureDisabled => FeatureOff(),
            LoraMutationStatus.NotFound => NotFound(),
            _ => NoContent()
        };
    }

    /// <summary>
    /// Binds this adapter to one of the caller's own custom agents (or clears it when
    /// <c>adapterId</c> is omitted from the route by passing an empty assign). Available to
    /// any authenticated user; the agent must be owned by the caller. 501 when off.
    /// </summary>
    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromQuery] Guid agentId, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (agentId == Guid.Empty) return BadRequest(new { message = "agentId là bắt buộc." });

        var result = await _mediator.Send(new AssignLoraAdapterCommand
        {
            TenantId = tenantId,
            UserId = userId,
            AgentId = agentId,
            LoraAdapterId = id
        }, ct);

        return result.Status switch
        {
            LoraAssignStatus.FeatureDisabled => FeatureOff(),
            LoraAssignStatus.AgentNotFound => NotFound(),
            LoraAssignStatus.AdapterNotFound => BadRequest(new { message = "Không tìm thấy adapter." }),
            _ => NoContent()
        };
    }

    /// <summary>Unbinds any adapter from one of the caller's own custom agents.</summary>
    [HttpDelete("assign")]
    public async Task<IActionResult> Unassign([FromQuery] Guid agentId, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (agentId == Guid.Empty) return BadRequest(new { message = "agentId là bắt buộc." });

        var result = await _mediator.Send(new AssignLoraAdapterCommand
        {
            TenantId = tenantId,
            UserId = userId,
            AgentId = agentId,
            LoraAdapterId = null
        }, ct);

        return result.Status switch
        {
            LoraAssignStatus.FeatureDisabled => FeatureOff(),
            LoraAssignStatus.AgentNotFound => NotFound(),
            _ => NoContent()
        };
    }

    private IActionResult FeatureOff() =>
        StatusCode(StatusCodes.Status501NotImplemented, new { message = "Tính năng LoRA chưa được bật." });
}

/// <summary>JSON body for PUT /api/lora-adapters/{id}. Every field is optional (null = unchanged).</summary>
public sealed class UpdateLoraAdapterRequest
{
    public string? Name { get; set; }
    public float? Scale { get; set; }
    public bool? IsActive { get; set; }
}
