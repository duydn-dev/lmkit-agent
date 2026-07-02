using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly OCRKnowledgeIngestionService _ocrIngestion;

    public ChatController(IMediator mediator, OCRKnowledgeIngestionService ocrIngestion)
    {
        _mediator = mediator;
        _ocrIngestion = ocrIngestion;
    }

    /// <summary>
    /// Stream chat completion — JSON body (text only, no files).
    /// </summary>
    [Authorize] // M6 Fix: was missing — chat endpoints must require authentication
    [HttpPost("stream")]
    public async Task StreamChatCompletion([FromBody] StreamChatCommand request, CancellationToken cancellationToken)
    {
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

        await foreach (var chunk in stream)
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            // Format SSE
            var message = $"data: {chunk}\n\n";
            await Response.WriteAsync(message, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        
        await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Stream chat completion WITH file attachments — multipart/form-data.
    /// Files are processed (OCR/converted), injected into context, and auto-saved to Qdrant.
    /// </summary>
    [Authorize] // M6 Fix: was missing — chat endpoints must require authentication
    [HttpPost("stream-with-files")]
    public async Task StreamChatWithFiles(
        [FromForm] string sessionId,
        [FromForm] string message,
        [FromForm] string? modelId,
        [FromForm] List<IFormFile>? files,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Step 1: Process file attachments
        var fileContextParts = new List<string>();
        if (files != null && files.Count > 0)
        {
            // Get tenantId from session
            var userIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
            var tenantIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "TenantId")?.Value;

            if (!Guid.TryParse(userIdString, out var currentUserId) || !Guid.TryParse(tenantIdString, out var tenantId))
            {
                Response.StatusCode = 401;
                await Response.WriteAsync("data: [ERROR: Unauthorized]\n\n");
                return;
            }

            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads", "ChatAttachments");
            Directory.CreateDirectory(uploadDir);

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                // Save file to disk
                var savedPath = Path.Combine(uploadDir, $"{Guid.NewGuid()}_{file.FileName}");
                using (var stream = new FileStream(savedPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }

                // Send SSE thinking step
                await WriteSseAsync($"[THINKING]: Đang xử lý file đính kèm: {file.FileName}...\\n", cancellationToken);

                // Process file (OCR/convert + auto-save to Qdrant)
                var result = await _ocrIngestion.ProcessFileForChatAsync(tenantId, savedPath, file.FileName, cancellationToken);
                
                if (result.Success)
                {
                    var truncated = result.ExtractedText.Length > 3000 
                        ? result.ExtractedText.Substring(0, 3000) + "... [Nội dung đã được lưu đầy đủ vào kho tri thức]"
                        : result.ExtractedText;
                    fileContextParts.Add($"[File: {result.FileName} ({result.FileType})]: {truncated}");
                    
                    await WriteSseAsync($"[THINKING]: ✅ Đã xử lý {file.FileName} ({result.FileType}) và lưu vào kho tri thức\\n", cancellationToken);
                }
                else
                {
                    await WriteSseAsync($"[THINKING]: ⚠️ Không thể xử lý {file.FileName}: {result.ErrorMessage}\\n", cancellationToken);
                }
            }
        }

        // Step 2: Build augmented message with file context
        var augmentedMessage = message;
        if (fileContextParts.Count > 0)
        {
            augmentedMessage = message + "\n\n--- Nội dung file đính kèm ---\n" + string.Join("\n\n", fileContextParts);
        }

        // Get UserId and TenantId for the command
        var finalUserIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        var finalTenantIdString = HttpContext.User.Claims.FirstOrDefault(c => c.Type == "TenantId")?.Value;
        Guid.TryParse(finalUserIdString, out var commandUserId);
        Guid.TryParse(finalTenantIdString, out var commandTenantId);

        var command = new StreamChatCommand
        {
            SessionId = Guid.TryParse(sessionId, out var sid) ? sid : Guid.Empty,
            UserId = commandUserId,
            TenantId = commandTenantId,
            Message = augmentedMessage,
            ModelId = modelId ?? "qwen3.5:2b"
        };

        var chatStream = _mediator.CreateStream(command, cancellationToken);

        await foreach (var chunk in chatStream)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await WriteSseAsync(chunk, cancellationToken);
        }

        await WriteSseAsync("[DONE]", cancellationToken);
    }

    private async Task WriteSseAsync(string data, CancellationToken ct)
    {
        var message = $"data: {data}\n\n";
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
