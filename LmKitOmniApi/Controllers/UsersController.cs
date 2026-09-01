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
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _)) return Unauthorized();

        var users = await _mediator.Send(new GetUsersQuery { TenantId = tenantId }, cancellationToken);
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _)) return Unauthorized();

        var result = await _mediator.Send(new CreateUserCommand
        {
            TenantId = tenantId,
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
            TenantId = tenantId,
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
            TenantId = tenantId,
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
