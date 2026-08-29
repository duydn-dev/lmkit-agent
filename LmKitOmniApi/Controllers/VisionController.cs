using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Models;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class VisionController : ApiControllerBase
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;
    private readonly ILogger<VisionController> _logger;

    public VisionController(IMediator mediator, UserResourceAccessService resources, ILogger<VisionController> logger)
    {
        _mediator = mediator;
        _resources = resources;
        _logger = logger;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeImage([FromBody] VisionAnalysisRequest request)
    {
        var path = ValidateOwnedPath(request.ImagePath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4_000)
            return BadRequest("Prompt must contain between 1 and 4000 characters.");
        if (!IsFileWithinLimit(path.SanitizedPath)) return BadRequest("Image exceeds the 20 MB limit.");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image analysis failed.");
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
        if (!IsFileWithinLimit(path.SanitizedPath)) return BadRequest("Image exceeds the 20 MB limit.");

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background removal failed.");
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
        if (request.Categories.Length > 100 || request.Categories.Any(category => string.IsNullOrWhiteSpace(category) || category.Length > 100))
            return BadRequest("Category limits were exceeded.");
        if (!IsFileWithinLimit(path.SanitizedPath)) return BadRequest("Image exceeds the 20 MB limit.");

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image classification failed.");
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
        if (!IsFileWithinLimit(path.SanitizedPath)) return BadRequest("Image exceeds the 20 MB limit.");

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image OCR failed.");
            return Problem(statusCode: 500, title: "Image OCR failed.");
        }
    }

    private PathValidationResult ValidateOwnedPath(string path)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return PathValidationResult.Deny("Authenticated tenant/user identity is missing.");
        return _resources.ValidateOwnedPath(tenantId, userId, path);
    }

    private static bool IsFileWithinLimit(string path) =>
        System.IO.File.Exists(path) && new FileInfo(path).Length <= MaximumImageBytes;
}
