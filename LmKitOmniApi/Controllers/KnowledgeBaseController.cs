using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Documents.Commands;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
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
        if (string.IsNullOrEmpty(command.Content))
            return BadRequest("Content cannot be empty.");

        try
        {
            var result = await _mediator.Send(command);
            return Ok(new { Message = result });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("query")]
    public async Task<IActionResult> QueryKnowledge([FromBody] QueryDocumentCommand command)
    {
        if (string.IsNullOrEmpty(command.Query))
            return BadRequest("Query cannot be empty.");

        try
        {
            var result = await _mediator.Send(command);
            return Ok(new { Answer = result });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
