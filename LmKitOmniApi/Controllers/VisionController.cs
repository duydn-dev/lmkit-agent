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

    [HttpPost("remove-background")]
    public async Task<IActionResult> RemoveBackground([FromBody] RemoveBackgroundRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath))
            return BadRequest("ImagePath cannot be empty.");

        try
        {
            var command = new RemoveBackgroundCommand { ImagePath = request.ImagePath };
            var result = await _mediator.Send(command);

            return Ok(new RemoveBackgroundResponse { Base64Image = result.Base64Image });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("classify")]
    public async Task<IActionResult> ClassifyImage([FromBody] ClassifyImageRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || request.Categories == null || request.Categories.Length == 0)
            return BadRequest("ImagePath and Categories must not be empty.");

        try
        {
            var command = new ClassifyImageCommand 
            { 
                ImagePath = request.ImagePath,
                Categories = request.Categories
            };
            var result = await _mediator.Send(command);

            return Ok(new ClassifyImageResponse 
            { 
                Category = result.Category,
                Confidence = result.Confidence 
            });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("ocr")]
    public async Task<IActionResult> ExtractTextFromImage([FromBody] ExtractTextFromImageRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath))
            return BadRequest("ImagePath cannot be empty.");

        try
        {
            var command = new ExtractTextFromImageCommand 
            { 
                ImagePath = request.ImagePath,
                IncludeCoordinates = request.IncludeCoordinates
            };
            var result = await _mediator.Send(command);

            return Ok(new ExtractTextFromImageResponse 
            { 
                Text = result.Text,
                Regions = result.Regions 
            });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
