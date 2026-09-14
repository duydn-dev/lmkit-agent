using LmKitOmniApi.Application.Common;
using LmKitOmniApi.Application.Tenants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Quản lý tenant (Admin). Getlist chuẩn phân trang + tìm kiếm; xóa chỉ được
/// với tenant RỖNG (command tự kiểm tra) — không bao giờ cascade dữ liệu.
/// <c>GET /options</c> là nguồn cho các dropdown gán tenant (vd. kết nối CSDL).
/// </summary>
[ApiController]
[Route("api/tenants")]
[Authorize(Roles = "Admin")]
public sealed class TenantsController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public TenantsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? search, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        return Ok(await _mediator.Send(new ListTenantsQuery { Page = p, PageSize = size, Search = search }, ct));
    }

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
        => Ok(await _mediator.Send(new GetTenantOptionsQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveTenantRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new CreateTenantCommand { Request = request }, ct);
        return result.Success ? Ok(new { id = result.Id }) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveTenantRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdateTenantCommand { Id = id, Request = request }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy tenant." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var actorTenantId)) return Unauthorized();
        var result = await _mediator.Send(new DeleteTenantCommand { Id = id, ActorTenantId = actorTenantId }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy tenant." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }
}
