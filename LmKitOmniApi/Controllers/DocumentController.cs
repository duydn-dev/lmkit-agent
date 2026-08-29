using LMKit.Document.Conversion;
using LmKitOmniApi.Models;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.RateLimiting;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentController : ApiControllerBase
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private readonly LmModelManager _modelManager;
    private readonly LmKitOmniApi.Infrastructure.Data.HermesDbContext _dbContext;
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;
    private readonly IVectorStoreService _vectorStore;
    private readonly string _vectorCollectionName;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(
        LmModelManager modelManager,
        LmKitOmniApi.Infrastructure.Data.HermesDbContext dbContext,
        IMediator mediator,
        UserResourceAccessService resources,
        IVectorStoreService vectorStore,
        IConfiguration configuration,
        ILogger<DocumentController> logger)
    {
        _modelManager = modelManager;
        _dbContext = dbContext;
        _mediator = mediator;
        _resources = resources;
        _vectorStore = vectorStore;
        _vectorCollectionName = configuration["VectorStore:CollectionName"] ?? "lmkit_chunks";
        _logger = logger;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetDocuments()
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
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
                d.UploadedAt,
                d.IsVectorized,
                d.VectorizationStatus,
                d.ProcessingAttempts,
                HasError = d.LastProcessingError != null
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
        if (!await UploadFileValidator.HasExpectedSignatureAsync(file, ext, HttpContext.RequestAborted))
            return BadRequest("File content does not match its extension.");

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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();
        var isAdmin = User.IsInRole("Admin");
        var document = await _dbContext.Documents
            .Include(item => item.User)
            .Include(item => item.Chunks)
            .FirstOrDefaultAsync(item => item.Id == id
                && item.User != null
                && item.User.TenantId == tenantId
                && (isAdmin || item.UserId == currentUserId), cancellationToken);
        if (document is null) return NotFound();

        var vectorIds = document.Chunks.Select(chunk => chunk.VectorId).Distinct().ToArray();
        if (vectorIds.Length > 0)
            await _vectorStore.DeleteVectorsAsync(_vectorCollectionName, vectorIds, cancellationToken);

        var ownedPath = _resources.ValidateOwnedPath(tenantId, document.UserId, document.FilePath);
        if (ownedPath.IsAllowed && System.IO.File.Exists(ownedPath.SanitizedPath))
            System.IO.File.Delete(ownedPath.SanitizedPath);

        _dbContext.Documents.Remove(document);
        await _dbContext.SaveChangesAsync(cancellationToken);
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
            DocumentToMarkdown converter;
            IAsyncDisposable? inferenceLease = null;

            if (request.Strategy.ToLower() == "vlmocr" || request.Strategy.ToLower() == "hybrid")
            {
                var ocrModel = await _modelManager.GetVisionModelAsync(ct: cancellationToken);
                inferenceLease = await _modelManager.AcquireVisionInferenceAsync(cancellationToken);
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
            
            try
            {
                var result = converter.Convert(path.SanitizedPath, options);

                return Ok(new DocumentConversionResponse
                {
                    Markdown = result.Markdown,
                    TotalPages = result.Pages.Count,
                    Elapsed = result.Elapsed
                });
            }
            finally
            {
                if (inferenceLease is not null)
                    await inferenceLease.DisposeAsync();
            }
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document data extraction failed for tenant {TenantId} user {UserId}.", tenantId, userId);
            return Problem(statusCode: 500, title: "Document extraction failed.");
        }
    }
}
