using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Documents.Commands;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IMediator _mediator;

    public KnowledgeBaseController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("ingest")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestDocumentCommand command)
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Knowledge ingestion failed.");
        }
    }

    [HttpPost("query")]
    public async Task<IActionResult> QueryKnowledge([FromBody] QueryDocumentCommand command)
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Knowledge query failed.");
        }
    }

    private bool TryGetIdentity(out Guid tenantId, out Guid userId)
    {
        var tenantValid = Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);
        var userValid = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        return tenantValid && userValid;
    }
}
