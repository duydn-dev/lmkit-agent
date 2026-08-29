using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Agents.Commands;
using LmKitOmniApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class AgentsController : ApiControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(IMediator mediator, ILogger<AgentsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [HttpPost("content-creation-pipeline")]
    public async Task<IActionResult> RunContentCreationPipeline([FromBody] ContentCreationPipelineRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Topic))
            return BadRequest("Topic cannot be empty.");
        if (request.Topic.Length > 2_000) return BadRequest("Topic cannot exceed 2000 characters.");
        if (!TryGetIdentity(out var tenantId, out var userId))
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent content-creation pipeline failed for tenant {TenantId} user {UserId}.", tenantId, userId);
            return Problem(statusCode: 500, title: "Agent execution failed.");
        }
    }
}
