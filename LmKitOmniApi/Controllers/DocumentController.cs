using LMKit.Document.Conversion;
using LmKitOmniApi.Models;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediatR;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentController : ControllerBase
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private readonly LmModelManager _modelManager;
    private readonly LmKitOmniApi.Infrastructure.Data.HermesDbContext _dbContext;
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;

    public DocumentController(
        LmModelManager modelManager,
        LmKitOmniApi.Infrastructure.Data.HermesDbContext dbContext,
        IMediator mediator,
        UserResourceAccessService resources)
    {
        _modelManager = modelManager;
        _dbContext = dbContext;
        _mediator = mediator;
        _resources = resources;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetDocuments()
    {
        var tenantIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "TenantId")?.Value;
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(tenantIdString, out var tenantId) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();
        var isAdmin = User.IsInRole("Admin");

        var docs = await _dbContext.Documents
            .Include(d => d.User)
            .Where(d => d.User != null
                && d.User.TenantId == tenantId
                && (isAdmin || d.UserId == userId))
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new {
                d.Id,
                d.FileName,
                d.FilePath,
                d.UploadedAt,
                d.IsVectorized
            })
            .ToListAsync();
            
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

        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();

        var uploadDir = _resources.GetUploadDirectory(tenantId, currentUserId);
        Directory.CreateDirectory(uploadDir);
        var safeFileName = Path.GetFileName(file.FileName);
        var filePath = Path.Combine(uploadDir, $"{Guid.NewGuid():N}{ext}");

        await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(stream, HttpContext.RequestAborted);
        }

        var doc = new LmKitOmniApi.Domain.Entities.Document
        {
            FileName = safeFileName,
            FilePath = filePath,
            UserId = currentUserId,
            IsVectorized = false
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(new { Message = "File uploaded successfully. Background job will vectorize it shortly.", DocumentId = doc.Id });
    }

    [HttpPost("convert")]
    public async Task<IActionResult> ConvertDocument([FromBody] DocumentConversionRequest request)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var path = _resources.ValidateOwnedPath(tenantId, userId, request.FilePath);
        if (!path.IsAllowed || !System.IO.File.Exists(path.SanitizedPath))
            return BadRequest("File not found or invalid path.");

        try
        {
            DocumentToMarkdown converter;

            if (request.Strategy.ToLower() == "vlmocr" || request.Strategy.ToLower() == "hybrid")
            {
                var ocrModel = await _modelManager.GetVisionModelAsync();
                converter = new DocumentToMarkdown(ocrModel);
            }
            else
            {
                converter = new DocumentToMarkdown();
            }

            var options = new DocumentToMarkdownOptions();
            if (Enum.TryParse<DocumentToMarkdownStrategy>(request.Strategy, true, out var strategy))
            {
                options.Strategy = strategy;
            }
            
            var result = converter.Convert(path.SanitizedPath, options);

            return Ok(new DocumentConversionResponse
            {
                Markdown = result.Markdown,
                TotalPages = result.Pages.Count,
                Elapsed = result.Elapsed
            });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Document conversion failed.");
        }
    }

    [HttpPost("extract-data")]
    public async Task<IActionResult> ExtractData([FromBody] ExtractDocumentDataRequest request)
    {
        if (string.IsNullOrEmpty(request.DocumentPath))
            return BadRequest("DocumentPath cannot be empty.");
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var path = _resources.ValidateOwnedPath(tenantId, userId, request.DocumentPath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new LmKitOmniApi.Application.Documents.Commands.ExtractDocumentDataCommand
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Document extraction failed.");
        }
    }

    private bool TryGetIdentity(out Guid tenantId, out Guid userId)
    {
        var tenantValid = Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);
        var userValid = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        return tenantValid && userValid;
    }
}
