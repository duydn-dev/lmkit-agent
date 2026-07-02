using LMKit.Document.Conversion;
using LmKitOmniApi.Models;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediatR;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentController : ControllerBase
{
    private readonly LmModelManager _modelManager;
    private readonly LmKitOmniApi.Infrastructure.Data.HermesDbContext _dbContext;
    private readonly IMediator _mediator;

    public DocumentController(LmModelManager modelManager, LmKitOmniApi.Infrastructure.Data.HermesDbContext dbContext, IMediator mediator)
    {
        _modelManager = modelManager;
        _dbContext = dbContext;
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetDocuments()
    {
        var docs = await _dbContext.Documents
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

    [HttpPost("upload")]
    public async Task<IActionResult> UploadDocument(IFormFile file, [FromForm] Guid userId)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded.");

        var allowedExtensions = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            return BadRequest($"Unsupported file type. Allowed extensions: {string.Join(", ", allowedExtensions)}");
        }

        var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, Guid.NewGuid().ToString() + "_" + file.FileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var doc = new LmKitOmniApi.Domain.Entities.Document
        {
            FileName = file.FileName,
            FilePath = filePath,
            UserId = userId,
            IsVectorized = false
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        return Ok(new { Message = "File uploaded successfully. Background job will vectorize it shortly.", DocumentId = doc.Id });
    }

    [HttpPost("convert")]
    public async Task<IActionResult> ConvertDocument([FromBody] DocumentConversionRequest request)
    {
        if (string.IsNullOrEmpty(request.FilePath) || !System.IO.File.Exists(request.FilePath))
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
            
            var result = converter.Convert(request.FilePath, options);

            return Ok(new DocumentConversionResponse
            {
                Markdown = result.Markdown,
                TotalPages = result.Pages.Count,
                Elapsed = result.Elapsed
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("extract-data")]
    public async Task<IActionResult> ExtractData([FromBody] ExtractDocumentDataRequest request)
    {
        if (string.IsNullOrEmpty(request.DocumentPath))
            return BadRequest("DocumentPath cannot be empty.");

        try
        {
            var command = new LmKitOmniApi.Application.Documents.Commands.ExtractDocumentDataCommand
            {
                DocumentPath = request.DocumentPath,
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
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
