using LmKitOmniApi.Application.Quotas;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Quản trị hạn mức: gói, gán gói cho đơn vị, grant cấp thêm và số dư token mua trước. Bốn thứ
/// này trước đây CHỈ vào được bằng insert SQL tay; đây là API thay thế đúng thao tác đó.
///
/// Toàn bộ route đều Admin, cùng lý do <see cref="TenantsController"/> như vậy: đây là số liệu
/// và quyền cấp phát GIỮA các đơn vị với nhau, không phải dữ liệu của một đơn vị.
/// </summary>
[ApiController]
[Route("api/admin/quota")]
[Authorize(Roles = "Admin")]
public sealed class QuotaAdminController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public QuotaAdminController(IMediator mediator) => _mediator = mediator;

    // ---------------------------------------------------------------- Gói

    [HttpGet("plans")]
    public async Task<IActionResult> ListPlans(CancellationToken ct)
        => Ok(await _mediator.Send(new ListPlansQuery(), ct));

    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan([FromBody] SavePlanRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new CreatePlanCommand { Request = request }, ct);
        return result.Success ? Ok(new { id = result.Id }) : BadRequest(new { message = result.Error });
    }

    [HttpPut("plans/{id:guid}")]
    public async Task<IActionResult> UpdatePlan(Guid id, [FromBody] SavePlanRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdatePlanCommand { Id = id, Request = request }, ct);
        return ToActionResult(result);
    }

    /// <summary>Ngừng dùng gói (không xoá — gói cũ vẫn tra cứu được cho các kỳ đã chạy).</summary>
    [HttpDelete("plans/{id:guid}")]
    public async Task<IActionResult> DeactivatePlan(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new DeactivatePlanCommand { Id = id }, ct);
        return ToActionResult(result);
    }

    // ---------------------------------------------------------------- Hạn mức theo đơn vị

    [HttpGet("tenants")]
    public async Task<IActionResult> ListTenantQuotas(CancellationToken ct)
        => Ok(await _mediator.Send(new ListTenantQuotasQuery(), ct));

    [HttpPut("tenants/{tenantId:guid}/plan")]
    public async Task<IActionResult> AssignPlan(
        Guid tenantId, [FromBody] AssignPlanRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var actorUserId)) return Unauthorized();

        var result = await _mediator.Send(new AssignPlanCommand
        {
            TenantId = tenantId,
            PlanId = request.PlanId,
            RenewalAtUtc = request.RenewalAtUtc,
            ActorUserId = actorUserId
        }, ct);
        return ToActionResult(result);
    }

    [HttpDelete("tenants/{tenantId:guid}/plan")]
    public async Task<IActionResult> RemovePlan(Guid tenantId, CancellationToken ct)
    {
        var result = await _mediator.Send(new RemovePlanCommand { TenantId = tenantId }, ct);
        return ToActionResult(result);
    }

    [HttpPut("tenants/{tenantId:guid}/credit")]
    public async Task<IActionResult> SetCredit(
        Guid tenantId, [FromBody] SaveCreditRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new SetTenantCreditCommand
        {
            TenantId = tenantId,
            Balance = request.Balance
        }, ct);
        return ToActionResult(result);
    }

    // ---------------------------------------------------------------- Grant

    [HttpGet("tenants/{tenantId:guid}/grants")]
    public async Task<IActionResult> ListGrants(Guid tenantId, CancellationToken ct)
        => Ok(await _mediator.Send(new ListGrantsQuery { TenantId = tenantId }, ct));

    [HttpPost("tenants/{tenantId:guid}/grants")]
    public async Task<IActionResult> CreateGrant(
        Guid tenantId, [FromBody] SaveGrantRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var actorUserId)) return Unauthorized();

        var result = await _mediator.Send(new CreateGrantCommand
        {
            TenantId = tenantId,
            Request = request,
            ActorUserId = actorUserId
        }, ct);
        return result.Success ? Ok(new { id = result.Id }) : ToActionResult(result);
    }

    [HttpDelete("grants/{grantId:guid}")]
    public async Task<IActionResult> DeleteGrant(Guid grantId, CancellationToken ct)
    {
        var result = await _mediator.Send(new DeleteGrantCommand { GrantId = grantId }, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Ánh xạ kết quả handler sang HTTP. <c>IsNotFound</c> do handler đặt (không so chuỗi thông
    /// báo) để "không tìm thấy" luôn là 404 và mọi lỗi luật là 400.
    /// </summary>
    private IActionResult ToActionResult(QuotaMutationResult result)
    {
        if (result.Success) return NoContent();
        if (result.IsNotFound) return NotFound(new { message = result.Error });
        return BadRequest(new { message = result.Error });
    }
}
