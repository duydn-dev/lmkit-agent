using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Models;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VisionController : ControllerBase
{
    private readonly IMediator _mediator;

    public VisionController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeImage([FromBody] VisionAnalysisRequest request)
    {
        try
        {
            var command = new AnalyzeImageCommand
            {
                ImagePath = request.ImagePath,
                Prompt = request.Prompt
            };

            var result = await _mediator.Send(command);

            return Ok(new
            {
                Text = result
            });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
