using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Infrastructure.AI;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private const long MaxAttachmentBytes = 20 * 1024 * 1024;
    private const long MaxTotalAttachmentBytes = 50 * 1024 * 1024;
    private const int MaxAttachmentCount = 8;
    private const int MaxMessageCharacters = 50_000;
    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tiff",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".md", ".csv", ".json", ".xml"
    };

    private readonly IMediator _mediator;
    private readonly OCRKnowledgeIngestionService _ocrIngestion;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IMediator mediator,
        OCRKnowledgeIngestionService ocrIngestion,
        ILogger<ChatController> logger)
    {
        _mediator = mediator;
        _ocrIngestion = ocrIngestion;
        _logger = logger;
    }

    /// <summary>
    /// Stream chat completion — JSON body (text only, no files).
    /// </summary>
    [Authorize] // M6 Fix: was missing — chat endpoints must require authentication
    [EnableRateLimiting("ai-agent")]
    [HttpPost("stream")]
    public async Task StreamChatCompletion([FromBody] StreamChatCommand request, CancellationToken cancellationToken)
    {
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaxMessageCharacters)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { message = $"SessionId and a message up to {MaxMessageCharacters} characters are required." }, cancellationToken);
            return;
        }
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { message = "Per-request model selection is not allowed." }, cancellationToken);
            return;
        }

        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        var tenantIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "TenantId")?.Value;

        if (!Guid.TryParse(userIdString, out var currentUserId) || !Guid.TryParse(tenantIdString, out var currentTenantId))
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized");
            return;
        }

        request.UserId = currentUserId;
        request.TenantId = currentTenantId;

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var stream = _mediator.CreateStream(request, cancellationToken);

        await StreamResponseAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Stream chat completion WITH file attachments — multipart/form-data.
    /// Files are processed (OCR/converted), injected into context, and auto-saved to Qdrant.
    /// </summary>
    [Authorize] // M6 Fix: was missing — chat endpoints must require authentication
    [EnableRateLimiting("ai-agent")]
    [HttpPost("stream-with-files")]
    public async Task StreamChatWithFiles(
        [FromForm] string sessionId,
        [FromForm] string message,
        [FromForm] string? modelId,
        [FromForm] List<IFormFile>? files,
        [FromForm] bool saveToKnowledge,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        var tenantIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "TenantId")?.Value;
        if (!Guid.TryParse(userIdString, out var currentUserId) || !Guid.TryParse(tenantIdString, out var tenantId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await WriteSseAsync("[ERROR: Unauthorized]", cancellationToken);
            return;
        }

        if (!Guid.TryParse(sessionId, out var parsedSessionId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteSseAsync("[ERROR: Invalid session id]", cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageCharacters)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteSseAsync($"[ERROR: Message must contain between 1 and {MaxMessageCharacters} characters]", cancellationToken);
            return;
        }
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteSseAsync("[ERROR: Per-request model selection is not allowed]", cancellationToken);
            return;
        }
        if (files is { Count: > MaxAttachmentCount })
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteSseAsync($"[ERROR: A maximum of {MaxAttachmentCount} attachments is allowed]", cancellationToken);
            return;
        }
        long totalAttachmentBytes = 0;
        foreach (var file in files ?? [])
        {
            if (file.Length > MaxTotalAttachmentBytes - totalAttachmentBytes)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteSseAsync("[ERROR: Total attachment size cannot exceed 50 MB]", cancellationToken);
                return;
            }
            totalAttachmentBytes += file.Length;
        }

        // Step 1: Process file attachments
        var fileContextParts = new List<string>();
        if (files != null && files.Count > 0)
        {
            var uploadDir = Path.Combine(
                Directory.GetCurrentDirectory(),
                "Uploads",
                tenantId.ToString("N"),
                currentUserId.ToString("N"),
                "ChatAttachments");
            Directory.CreateDirectory(uploadDir);

            foreach (var file in files)
            {
                if (file.Length == 0) continue;
                if (file.Length > MaxAttachmentBytes)
                {
                    await WriteSseAsync($"[THINKING]: ⚠️ Bỏ qua file quá 20 MB: {Path.GetFileName(file.FileName)}\\n", cancellationToken);
                    continue;
                }

                var safeFileName = Path.GetFileName(file.FileName);
                var extension = Path.GetExtension(safeFileName);
                if (string.IsNullOrWhiteSpace(safeFileName) || !AllowedAttachmentExtensions.Contains(extension))
                {
                    await WriteSseAsync($"[THINKING]: ⚠️ Loại file không được hỗ trợ: {safeFileName}\\n", cancellationToken);
                    continue;
                }
                if (!await UploadFileValidator.HasExpectedSignatureAsync(file, extension, cancellationToken))
                {
                    await WriteSseAsync($"[THINKING]: ⚠️ Nội dung file không khớp phần mở rộng: {safeFileName}\n", cancellationToken);
                    continue;
                }

                // The local copy is a processing scratch file. Knowledge persistence
                // stores extracted chunks in the vector store, not this unbounded upload.
                var savedPath = Path.Combine(uploadDir, $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
                try
                {
                    await using (var stream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await file.CopyToAsync(stream, cancellationToken);
                    }

                    await WriteSseAsync($"[THINKING]: Đang xử lý file đính kèm: {file.FileName}...\\n", cancellationToken);

                    var result = await _ocrIngestion.ProcessFileForChatAsync(
                        tenantId,
                        currentUserId,
                        savedPath,
                        safeFileName,
                        saveToKnowledge,
                        cancellationToken);
                    
                    if (result.Success)
                    {
                        var truncated = result.ExtractedText.Length > 3000
                            ? result.ExtractedText.Substring(0, 3000) + "... [Nội dung đã được lưu đầy đủ vào kho tri thức]"
                            : result.ExtractedText;
                        fileContextParts.Add($"[File: {result.FileName} ({result.FileType})]: {truncated}");

                        var persistenceMessage = saveToKnowledge ? " và lưu vào kho tri thức" : string.Empty;
                        await WriteSseAsync($"[THINKING]: ✅ Đã xử lý {safeFileName} ({result.FileType}){persistenceMessage}\\n", cancellationToken);
                    }
                    else
                    {
                        await WriteSseAsync($"[THINKING]: ⚠️ Không thể xử lý {file.FileName}: {result.ErrorMessage}\\n", cancellationToken);
                    }
                }
                finally
                {
                    try
                    {
                        if (System.IO.File.Exists(savedPath)) System.IO.File.Delete(savedPath);
                        if (Directory.Exists(uploadDir) && !Directory.EnumerateFileSystemEntries(uploadDir).Any())
                            Directory.Delete(uploadDir);
                    }
                    catch (Exception cleanupError)
                    {
                        _logger.LogWarning(cleanupError, "Unable to delete temporary chat attachment {Path}", savedPath);
                    }
                }
            }
        }

        // Step 2: Build augmented message with file context
        var augmentedMessage = message;
        if (fileContextParts.Count > 0)
        {
            augmentedMessage = message + "\n\n--- Nội dung file đính kèm ---\n" + string.Join("\n\n", fileContextParts);
        }

        var command = new StreamChatCommand
        {
            SessionId = parsedSessionId,
            UserId = currentUserId,
            TenantId = tenantId,
            Message = augmentedMessage,
            ModelId = string.Empty
        };

        var chatStream = _mediator.CreateStream(command, cancellationToken);

        await StreamResponseAsync(chatStream, cancellationToken);
    }

    private async Task StreamResponseAsync(IAsyncEnumerable<string> stream, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in stream.WithCancellation(ct))
                await WriteSseAsync(chunk, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat stream failed after response headers were sent");
            await WriteSseAsync("[ERROR]: Unable to generate a response.", ct);
        }

        await WriteSseAsync("[DONE]", ct);
    }

    private async Task WriteSseAsync(string data, CancellationToken ct)
    {
        // JSON encoding keeps newlines and control characters inside one SSE
        // event and prevents model output from injecting fake SSE fields.
        var message = $"data: {JsonSerializer.Serialize(data)}\n\n";
        await Response.WriteAsync(message, ct);
        await Response.Body.FlushAsync(ct);
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken cancellationToken)
    {
        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "id" || c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdString, out var currentUserId))
        {
             return Unauthorized();
        }

        var query = new GetChatSessionsQuery { UserId = currentUserId };
        var result = await _mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [Authorize]
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(CancellationToken cancellationToken)
    {
        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "id" || c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdString, out var currentUserId)) return Unauthorized();

        var command = new CreateChatSessionCommand { UserId = currentUserId };
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    [Authorize]
    [HttpGet("sessions/{id}/messages")]
    public async Task<IActionResult> GetSessionMessages(Guid id, CancellationToken cancellationToken)
    {
        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "id" || c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdString, out var currentUserId)) return Unauthorized();

        var query = new GetChatMessagesQuery { SessionId = id, UserId = currentUserId };
        var result = await _mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    [Authorize]
    [HttpDelete("sessions/{id}")]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken cancellationToken)
    {
        var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "id" || c.Type == ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdString, out var currentUserId)) return Unauthorized();

        var command = new DeleteChatSessionCommand { SessionId = id, UserId = currentUserId };
        var result = await _mediator.Send(command, cancellationToken);
        if (!result) return NotFound();
        return Ok(true);
    }
}
