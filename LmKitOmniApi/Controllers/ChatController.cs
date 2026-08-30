using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Chat.Queries;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Models;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ApiControllerBase
{
    private const long MaxAttachmentBytes = 20 * 1024 * 1024;
    private const long MaxTotalAttachmentBytes = 50 * 1024 * 1024;
    private const int MaxAttachmentCount = 8;
    private const int MaxMessageCharacters = 50_000;
    private const int MaxSessionTitleLength = 100;
    private const int MaxSearchQueryLength = 200;
    private const int MaxInlineFileContextChars = 3000;
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
        if (request.Regenerate && request.ReplaceLastExchange)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { message = "Không thể vừa tạo lại câu trả lời vừa chỉnh sửa tin nhắn cuối trong cùng một yêu cầu." }, cancellationToken);
            return;
        }
        // With regenerate the incoming message is ignored (the last user message is
        // re-run), so only require/validate it for normal and edit-last sends.
        if (request.SessionId == Guid.Empty || (!request.Regenerate && (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaxMessageCharacters)))
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

        if (!TryGetIdentity(out var currentTenantId, out var currentUserId))
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
        [FromForm] bool? enableWebSearch,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        if (!TryGetIdentity(out var tenantId, out var currentUserId))
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
                        var truncated = result.ExtractedText.Length > MaxInlineFileContextChars
                            ? result.ExtractedText.Substring(0, MaxInlineFileContextChars) + "... [Nội dung đã được lưu đầy đủ vào kho tri thức]"
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
            ModelId = string.Empty,
            // Honor the composer's web-search toggle on the multipart path too.
            // Nullable → omitting the field preserves today's default-on behavior;
            // an explicit false now reaches the orchestrator instead of being ignored.
            EnableWebSearch = enableWebSearch ?? true
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

    /// <summary>
    /// The caller's chat sessions, newest first. Optional <c>?projectId=</c>
    /// narrows the list to the sessions of that project (exact match); omitting
    /// it keeps the pre-existing full-list behavior unchanged.
    /// </summary>
    [Authorize]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions([FromQuery] Guid? projectId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId)) return Unauthorized();

        var query = new GetChatSessionsQuery { UserId = currentUserId, ProjectId = projectId };
        var result = await _mediator.Send(query, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Creates a chat session. Accepts an OPTIONAL JSON body
    /// <c>{ "customAgentId": guid?, "projectId": guid? }</c> to bind a custom
    /// agent and/or create the session inside a project; the body is read
    /// manually so legacy no-body clients keep working unchanged (a [FromBody]
    /// parameter would reject empty requests with an automatic 400).
    /// </summary>
    [Authorize]
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId)) return Unauthorized();

        Guid? customAgentId = null;
        Guid? projectId = null;
        // Content-Length is absent on chunked requests (TestServer, some HTTP
        // clients), so gate on the content type and tolerate an empty body
        // instead of trusting the header.
        if (Request.HasJsonContentType())
        {
            using var reader = new StreamReader(Request.Body);
            var rawBody = await reader.ReadToEndAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                CreateChatSessionRequestBody? body;
                try
                {
                    body = JsonSerializer.Deserialize<CreateChatSessionRequestBody>(rawBody, JsonSerializerOptions.Web);
                }
                catch (JsonException)
                {
                    return BadRequest(new { message = "Nội dung yêu cầu không hợp lệ." });
                }
                customAgentId = body?.CustomAgentId;
                projectId = body?.ProjectId;
            }
        }

        var command = new CreateChatSessionCommand { UserId = currentUserId, CustomAgentId = customAgentId, ProjectId = projectId };
        var result = await _mediator.Send(command, cancellationToken);
        if (result.ErrorMessage is not null)
            return BadRequest(new { message = result.ErrorMessage });
        return Ok(result.Session);
    }

    [Authorize]
    [HttpGet("sessions/{id}/messages")]
    public async Task<IActionResult> GetSessionMessages(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId)) return Unauthorized();

        var query = new GetChatMessagesQuery { SessionId = id, UserId = currentUserId };
        var result = await _mediator.Send(query, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [Authorize]
    [HttpDelete("sessions/{id}")]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId)) return Unauthorized();

        var command = new DeleteChatSessionCommand { SessionId = id, UserId = currentUserId };
        var result = await _mediator.Send(command, cancellationToken);
        if (!result) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Rename a chat session. 204 on success; 404 when the session does not exist
    /// or belongs to another tenant/user (never 403, so ids are not enumerable).
    /// </summary>
    [Authorize]
    [HttpPatch("sessions/{id}")]
    public async Task<IActionResult> RenameSession(Guid id, [FromBody] RenameChatSessionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { message = "Tiêu đề đoạn chat không được để trống." });
        if (title.Length > MaxSessionTitleLength)
            return BadRequest(new { message = $"Tiêu đề đoạn chat không được vượt quá {MaxSessionTitleLength} ký tự." });

        var command = new RenameChatSessionCommand
        {
            SessionId = id,
            TenantId = tenantId,
            UserId = currentUserId,
            Title = title
        };
        var renamed = await _mediator.Send(command, cancellationToken);
        return renamed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Search the caller's chat sessions by title or message content.
    /// Empty/whitespace <paramref name="q"/> returns the normal full list.
    /// </summary>
    [Authorize]
    [HttpGet("sessions/search")]
    public async Task<IActionResult> SearchSessions([FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var currentUserId)) return Unauthorized();

        if (q is { Length: > MaxSearchQueryLength })
            return BadRequest(new { message = $"Từ khóa tìm kiếm không được vượt quá {MaxSearchQueryLength} ký tự." });

        var query = new GetChatSessionsSearchQuery
        {
            TenantId = tenantId,
            UserId = currentUserId,
            Q = q ?? string.Empty
        };
        var result = await _mediator.Send(query, cancellationToken);
        return Ok(result);
    }
}
