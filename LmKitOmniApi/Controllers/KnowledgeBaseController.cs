using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.KnowledgeBase.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class KnowledgeBaseController : ApiControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<KnowledgeBaseController> _logger;

    public KnowledgeBaseController(IMediator mediator, ILogger<KnowledgeBaseController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [HttpPost("ingest")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestKnowledgeCommand command)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        command.TenantId = tenantId;
        command.UserId = userId;
        if (string.IsNullOrEmpty(command.Content))
            return BadRequest("Content cannot be empty.");

        try
        {
            var result = await _mediator.Send(command);
            return Ok(new { Message = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge ingestion failed for tenant {TenantId} user {UserId}.", tenantId, userId);
            return Problem(statusCode: 500, title: "Knowledge ingestion failed.");
        }
    }

    [HttpPost("query")]
    public async Task<IActionResult> QueryKnowledge([FromBody] QueryKnowledgeCommand command)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        command.TenantId = tenantId;
        command.UserId = userId;
        if (string.IsNullOrEmpty(command.Query))
            return BadRequest("Query cannot be empty.");

        try
        {
            var result = await _mediator.Send(command);
            return Ok(new { Answer = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge query failed for tenant {TenantId} user {UserId}.", tenantId, userId);
            return Problem(statusCode: 500, title: "Knowledge query failed.");
        }
    }
}
