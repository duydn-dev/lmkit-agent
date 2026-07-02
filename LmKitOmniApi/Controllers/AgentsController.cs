using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Agents.Commands;
using LmKitOmniApi.Models;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("content-creation-pipeline")]
    public async Task<IActionResult> RunContentCreationPipeline([FromBody] ContentCreationPipelineRequest request)
    {
        if (string.IsNullOrEmpty(request.Topic))
            return BadRequest("Topic cannot be empty.");

        try
        {
            var command = new RunContentCreationPipelineCommand { Topic = request.Topic };
            var result = await _mediator.Send(command);

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
