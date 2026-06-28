using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;
using LMKit.Document.Conversion;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// OCR Knowledge Ingestion Service — automatically saves OCR/conversion
/// results from chat file attachments into Qdrant for future recall.
/// Addresses the gap: "OCR ảnh/PDF trong chat KHÔNG lưu tri thức".
/// </summary>
public class OCRKnowledgeIngestionService
{
    private readonly IRagPipelineService _ragService;
    private readonly LmModelManager _modelManager;
    private readonly IMediator _mediator;
    private readonly ILogger<OCRKnowledgeIngestionService> _logger;

    public OCRKnowledgeIngestionService(
        IRagPipelineService ragService,
        LmModelManager modelManager,
        IMediator mediator,
        ILogger<OCRKnowledgeIngestionService> logger)
    {
        _ragService = ragService;
        _modelManager = modelManager;
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Process a file attachment in chat context: extract text, inject into AI context,
    /// and auto-save to Qdrant for future knowledge recall.
    /// Returns the extracted text content for the AI to use in response.
    /// </summary>
    public async Task<FileProcessingResult> ProcessFileForChatAsync(
        Guid tenantId, string filePath, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        string extractedText;

        try
        {
            if (IsImageFile(ext))
            {
                // OCR for images
                _logger.LogInformation("📎 Processing image attachment: {File}", fileName);
                extractedText = await _mediator.Send(new AnalyzeImageCommand
                {
                    ImagePath = filePath,
                    Prompt = "Hãy đọc và trích xuất toàn bộ văn bản trong hình ảnh này. Nếu không có chữ, hãy mô tả chi tiết nội dung hình ảnh."
                }, ct);
            }
            else if (IsDocumentFile(ext))
            {
                // Document conversion for PDF, DOCX, etc.
                _logger.LogInformation("📎 Converting document attachment: {File}", fileName);
                var converter = new DocumentToMarkdown();
                var result = converter.Convert(filePath, new DocumentToMarkdownOptions());
                extractedText = result.Markdown;
            }
            else
            {
                // Plain text files
                _logger.LogInformation("📎 Reading text attachment: {File}", fileName);
                extractedText = await File.ReadAllTextAsync(filePath, ct);
            }

            // Auto-save to Qdrant for future recall
            if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 10)
            {
                _logger.LogInformation("💾 Auto-saving attachment content to knowledge base: {File}", fileName);
                await _ragService.IngestDocumentAsync(tenantId, $"ChatAttachment_{fileName}", extractedText);
            }

            return new FileProcessingResult
            {
                Success = true,
                FileName = fileName,
                ExtractedText = extractedText,
                FileType = GetFileCategory(ext)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to process attachment: {File}", fileName);
            return new FileProcessingResult
            {
                Success = false,
                FileName = fileName,
                ErrorMessage = ex.Message
            };
        }
    }

    private static bool IsImageFile(string ext) =>
        ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif" or ".tiff";

    private static bool IsDocumentFile(string ext) =>
        ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx";

    private static string GetFileCategory(string ext) => ext switch
    {
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif" or ".tiff" => "image",
        ".pdf" => "pdf",
        ".doc" or ".docx" => "word",
        ".xls" or ".xlsx" => "excel",
        ".ppt" or ".pptx" => "powerpoint",
        _ => "text"
    };
}

public class FileProcessingResult
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
