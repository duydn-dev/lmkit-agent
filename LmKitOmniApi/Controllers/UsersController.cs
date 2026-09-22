using LmKitOmniApi.Application.Users;
using LmKitOmniApi.Application.Users.Commands;
using LmKitOmniApi.Application.Users.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")] // Chỉ Admin mới được truy cập các API này
public class UsersController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public UsersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? search,
        [FromQuery] Guid? tenantId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out _, out _)) return Unauthorized();

        var (p, size) = Application.Common.Paging.Normalize(page, pageSize);
        var users = await _mediator.Send(new GetUsersQuery
        {
            // Admin-only endpoint: mặc định liệt kê user của MỌI tenant (full quyền
            // hệ thống); ?tenantId=… lọc theo một tenant khi UI cần.
            TenantId = tenantId,
            Page = p,
            PageSize = size,
            Search = search
        }, cancellationToken);
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _)) return Unauthorized();

        var result = await _mediator.Send(new CreateUserCommand
        {
            TenantId = tenantId,
            // Admin có thể gán user mới vào tenant bất kỳ (quản lý đa tenant);
            // bỏ trống → vào tenant của admin.
            TargetTenantId = request.TenantId,
            Email = request.Email,
            Password = request.Password,
            FullName = request.FullName,
            Role = request.Role
        }, cancellationToken);

        if (result.Status == UserMutationStatus.ValidationFailed)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.User);
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateUserRoleCommand
        {
            ActorUserId = actorId,
            TargetUserId = id,
            Role = request.Role
        }, cancellationToken);

        return result.Status switch
        {
            UserMutationStatus.NotFound => NotFound(new { message = result.ErrorMessage }),
            UserMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => Ok(new { message = result.Message, role = result.Role })
        };
    }

    [HttpPut("{id}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorId)) return Unauthorized();

        var result = await _mediator.Send(new ToggleUserStatusCommand
        {
            ActorUserId = actorId,
            TargetUserId = id
        }, cancellationToken);

        return result.Status switch
        {
            UserMutationStatus.NotFound => NotFound(new { message = result.ErrorMessage }),
            UserMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => Ok(new { message = result.Message, isActive = result.IsActive })
        };
    }
}
