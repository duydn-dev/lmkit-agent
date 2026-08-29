using LmKitOmniApi.Models;
using Microsoft.AspNetCore.Mvc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using LmKitOmniApi.Application.Documents.Commands;
using LmKitOmniApi.Application.Documents.Queries;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.RateLimiting;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Presentation-only controller: multipart/IFormFile handling, claim parsing,
/// path-ownership validation and HTTP mapping. All LM-Kit, vector-store and
/// database work lives in the MediatR handlers under Application/Documents.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentController : ApiControllerBase
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(
        IMediator mediator,
        UserResourceAccessService resources,
        ILogger<DocumentController> logger)
    {
        _mediator = mediator;
        _resources = resources;
        _logger = logger;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetDocuments()
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var isAdmin = User.IsInRole("Admin");

        var docs = await _mediator.Send(new ListDocumentsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            IsAdmin = isAdmin
        }, HttpContext.RequestAborted);

        return Ok(docs);
    }

    [Authorize]
    [HttpPost("upload")]
    public async Task<IActionResult> UploadDocument(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded.");
        if (file.Length > MaxUploadBytes) return BadRequest("File exceeds the 50 MB limit.");

        var allowedExtensions = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            return BadRequest($"Unsupported file type. Allowed extensions: {string.Join(", ", allowedExtensions)}");
        }
        if (!await UploadFileValidator.HasExpectedSignatureAsync(file, ext, HttpContext.RequestAborted))
            return BadRequest("File content does not match its extension.");

        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();

        var safeFileName = Path.GetFileName(file.FileName);
        await using var content = file.OpenReadStream();

        var documentId = await _mediator.Send(new SaveUploadedDocumentCommand
        {
            TenantId = tenantId,
            UserId = currentUserId,
            FileName = safeFileName,
            Extension = ext,
            Content = content
        }, HttpContext.RequestAborted);

        return Ok(new { Message = "File uploaded successfully. Background job will vectorize it shortly.", DocumentId = documentId });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();
        var isAdmin = User.IsInRole("Admin");

        var deleted = await _mediator.Send(new DeleteDocumentCommand
        {
            DocumentId = id,
            TenantId = tenantId,
            UserId = currentUserId,
            IsAdmin = isAdmin
        }, cancellationToken);

        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("convert")]
    [EnableRateLimiting("ai-agent")]
    public async Task<IActionResult> ConvertDocument(
        [FromBody] DocumentConversionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var path = _resources.ValidateOwnedPath(tenantId, userId, request.FilePath);
        if (!path.IsAllowed || !System.IO.File.Exists(path.SanitizedPath))
            return BadRequest("File not found or invalid path.");

        try
        {
            var result = await _mediator.Send(new ConvertDocumentCommand
            {
                FilePath = path.SanitizedPath,
                Strategy = request.Strategy
            }, cancellationToken);

            return Ok(new DocumentConversionResponse
            {
                Markdown = result.Markdown,
                TotalPages = result.TotalPages,
                Elapsed = result.Elapsed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document conversion failed for tenant {TenantId} user {UserId} using strategy {Strategy}.", tenantId, userId, request.Strategy);
            return Problem(statusCode: 500, title: "Document conversion failed.");
        }
    }

    [HttpPost("extract-data")]
    [EnableRateLimiting("ai-agent")]
    public async Task<IActionResult> ExtractData([FromBody] ExtractDocumentDataRequest request)
    {
        if (string.IsNullOrEmpty(request.DocumentPath))
            return BadRequest("DocumentPath cannot be empty.");
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var path = _resources.ValidateOwnedPath(tenantId, userId, request.DocumentPath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new ExtractDocumentDataCommand
            {
                DocumentPath = path.SanitizedPath,
                JsonSchema = request.JsonSchema
            };
            var result = await _mediator.Send(command);

            return Ok(new ExtractDocumentDataResponse
            {
                JsonData = result.JsonData
            });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document data extraction failed for tenant {TenantId} user {UserId}.", tenantId, userId);
            return Problem(statusCode: 500, title: "Document extraction failed.");
        }
    }
}
