using LmKitOmniApi.Application.ToolPolicy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Chính sách thực thi tool của đơn vị (mức rủi ro → tự chạy / cần duyệt / chặn).
///
/// <para>
/// Trước đây bảng này chỉ tồn tại trong mã nguồn, nên câu hỏi vận hành "hệ thống tôi đang cho agent
/// tự chạy cái gì, và cái gì chờ người duyệt" phải trả lời bằng cách đọc code. Endpoint này đưa ra
/// đúng câu trả lời đó, và cho phép <b>siết thêm</b> cho một đơn vị cụ thể (xem
/// <see cref="ToolPolicyMutation"/>: không có chiều nới qua API).
/// </para>
///
/// <para>Admin đơn vị; thay đổi được audit tự động qua <c>AuditSaveChangesInterceptor</c>.</para>
/// </summary>
[ApiController]
[Route("api/admin/tool-policy")]
[Authorize(Roles = "Admin")]
public sealed class ToolPolicyController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public ToolPolicyController(IMediator mediator) => _mediator = mediator;

    /// <summary>GET /api/admin/tool-policy — mức nền, cấu hình đơn vị và kết quả cuối của mọi tool.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        return Ok(await _mediator.Send(new ListToolPoliciesQuery { TenantId = tenantId }, ct));
    }

    /// <summary>PUT /api/admin/tool-policy/{tool} — siết chặt hơn mức mặc định cho một tool.</summary>
    [HttpPut("{tool}")]
    public async Task<IActionResult> Set(
        string tool, [FromBody] SetToolPolicyRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new SetToolPolicyCommand
        {
            TenantId = tenantId,
            UserId = userId,
            ToolName = tool,
            Enforcement = request.Enforcement,
            Reason = request.Reason
        }, ct);

        return result.Status switch
        {
            // Lý do từ chối trả về NGUYÊN VĂN: đây là chỗ duy nhất nói cho admin biết vì sao không
            // được nới, và nói được lý do là cách duy nhất để họ sửa đúng chỗ (bảng phân loại).
            "invalid" => BadRequest(new { error = result.Error }),
            "not-found" => NotFound(),
            _ => Ok(result.Entry)
        };
    }

    /// <summary>DELETE /api/admin/tool-policy/{tool} — trả một tool về mức mặc định của hệ thống.</summary>
    [HttpDelete("{tool}")]
    public async Task<IActionResult> Clear(string tool, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(
            new ClearToolPolicyCommand { TenantId = tenantId, ToolName = tool }, ct);

        return result.Status switch
        {
            "not-found" => NotFound(),
            _ => Ok(result.Entry)
        };
    }

    public sealed record SetToolPolicyRequest(string Enforcement, string? Reason);
}
