using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Models;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VisionController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;

    public VisionController(IMediator mediator, UserResourceAccessService resources)
    {
        _mediator = mediator;
        _resources = resources;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeImage([FromBody] VisionAnalysisRequest request)
    {
        var path = ValidateOwnedPath(request.ImagePath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);
        try
        {
            var command = new AnalyzeImageCommand
            {
                ImagePath = path.SanitizedPath,
                Prompt = request.Prompt
            };

            var result = await _mediator.Send(command);

            return Ok(new
            {
                Text = result
            });
        }
        catch (FileNotFoundException)
        {
            return BadRequest("The requested image was not found.");
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Image analysis failed.");
        }
    }

    [HttpPost("remove-background")]
    public async Task<IActionResult> RemoveBackground([FromBody] RemoveBackgroundRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath))
            return BadRequest("ImagePath cannot be empty.");
        var path = ValidateOwnedPath(request.ImagePath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new RemoveBackgroundCommand { ImagePath = path.SanitizedPath };
            var result = await _mediator.Send(command);

            return Ok(new RemoveBackgroundResponse { Base64Image = result.Base64Image });
        }
        catch (FileNotFoundException)
        {
            return NotFound("The requested image was not found.");
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Background removal failed.");
        }
    }

    [HttpPost("classify")]
    public async Task<IActionResult> ClassifyImage([FromBody] ClassifyImageRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || request.Categories == null || request.Categories.Length == 0)
            return BadRequest("ImagePath and Categories must not be empty.");
        var path = ValidateOwnedPath(request.ImagePath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new ClassifyImageCommand 
            { 
                ImagePath = path.SanitizedPath,
                Categories = request.Categories
            };
            var result = await _mediator.Send(command);

            return Ok(new ClassifyImageResponse 
            { 
                Category = result.Category,
                Confidence = result.Confidence 
            });
        }
        catch (FileNotFoundException)
        {
            return NotFound("The requested image was not found.");
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Image classification failed.");
        }
    }

    [HttpPost("ocr")]
    public async Task<IActionResult> ExtractTextFromImage([FromBody] ExtractTextFromImageRequest request)
    {
        if (string.IsNullOrEmpty(request.ImagePath))
            return BadRequest("ImagePath cannot be empty.");
        var path = ValidateOwnedPath(request.ImagePath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new ExtractTextFromImageCommand 
            { 
                ImagePath = path.SanitizedPath,
                IncludeCoordinates = request.IncludeCoordinates
            };
            var result = await _mediator.Send(command);

            return Ok(new ExtractTextFromImageResponse 
            { 
                Text = result.Text,
                Regions = result.Regions 
            });
        }
        catch (FileNotFoundException)
        {
            return NotFound("The requested image was not found.");
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Image OCR failed.");
        }
    }

    private PathValidationResult ValidateOwnedPath(string path)
    {
        if (!Guid.TryParse(User.FindFirst("TenantId")?.Value, out var tenantId)
            || !Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return PathValidationResult.Deny("Authenticated tenant/user identity is missing.");
        return _resources.ValidateOwnedPath(tenantId, userId, path);
    }
}
