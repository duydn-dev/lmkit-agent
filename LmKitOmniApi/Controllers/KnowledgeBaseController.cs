using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Documents.Commands;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IMediator _mediator;

    public KnowledgeBaseController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestDocumentCommand command)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        command.TenantId = tenantId;
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
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        command.TenantId = tenantId;
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

    private bool TryGetTenantId(out Guid tenantId) => Guid.TryParse(
        User.FindFirst("TenantId")?.Value,
        out tenantId);
}
