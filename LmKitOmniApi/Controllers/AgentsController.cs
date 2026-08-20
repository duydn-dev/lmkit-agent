using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Agents.Commands;
using LmKitOmniApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class AgentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("content-creation-pipeline")]
    public async Task<IActionResult> RunContentCreationPipeline([FromBody] ContentCreationPipelineRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Topic))
            return BadRequest("Topic cannot be empty.");
        if (request.Topic.Length > 2_000) return BadRequest("Topic cannot exceed 2000 characters.");
        if (!Guid.TryParse(User.FindFirst("TenantId")?.Value, out var tenantId)
            || !Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return Unauthorized();

        try
        {
            var command = new RunContentCreationPipelineCommand
            {
                Topic = request.Topic,
                TenantId = tenantId,
                UserId = userId,
                UserRole = User.IsInRole("Admin") ? "Admin" : "User"
            };
            var result = await _mediator.Send(command, ct);

            return Ok(result);
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Agent execution failed.");
        }
    }
}
