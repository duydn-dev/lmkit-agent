using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Models;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class SpeechController : ApiControllerBase
{
    /// <summary>Browser recordings are short; 25 MB covers several minutes of audio.</summary>
    private const long MaxUploadedAudioBytes = 25L * 1024 * 1024;

    /// <summary>Request cap slightly above the file cap to leave room for multipart framing.</summary>
    private const long MaxUploadedAudioRequestBytes = 26L * 1024 * 1024;

    /// <summary>
    /// Accepted upload formats. UploadFileValidator has no audio signatures (it only covers
    /// documents/images), so this endpoint relies on size + content-type/extension checks and
    /// lets the LMKit audio decoder reject payloads it cannot parse.
    /// </summary>
    private static readonly HashSet<string> AllowedAudioContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/webm", "audio/ogg", "audio/wav", "audio/mpeg", "audio/mp4"
    };

    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".ogg", ".oga", ".wav", ".mp3", ".m4a", ".mp4"
    };

    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;
    private readonly ILogger<SpeechController> _logger;

    public SpeechController(IMediator mediator, UserResourceAccessService resources, ILogger<SpeechController> logger)
    {
        _mediator = mediator;
        _resources = resources;
        _logger = logger;
    }

    [HttpPost("transcribe")]
    public async Task<IActionResult> TranscribeAudio([FromBody] SpeechTranscriptionRequest request)
    {
        var path = ValidateOwnedPath(request.AudioPath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);
        try
        {
            var command = new TranscribeAudioCommand
            {
                AudioPath = path.SanitizedPath,
                EnableVad = request.EnableVad
            };

            var result = await _mediator.Send(command);

            return Ok(new
            {
                Text = result.Text,
                Duration = result.DurationSeconds
            });
        }
        catch (FileNotFoundException)
        {
            return BadRequest("The requested audio file was not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio transcription failed.");
            return Problem(statusCode: 500, title: "Audio transcription failed.");
        }
    }

    /// <summary>
    /// Browser transcription: accepts a recorded audio blob as multipart form data (field
    /// <c>audio</c>), stores it in the caller's scratch area (same pattern as chat
    /// attachments), transcribes it through the existing <see cref="TranscribeAudioCommand"/>
    /// pipeline with VAD enabled, and always deletes the scratch file afterwards.
    /// Wire contract: 200 <c>{ text }</c>; errors are 400/503 <c>{ message }</c> (Vietnamese).
    /// </summary>
    [HttpPost("transcribe-upload")]
    [RequestSizeLimit(MaxUploadedAudioRequestBytes)]
    public async Task<IActionResult> TranscribeUploadedAudio([FromForm] IFormFile? audio, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        if (audio is null || audio.Length == 0)
            return BadRequest(new { message = "Vui lòng gửi kèm file âm thanh trong trường 'audio'." });
        if (audio.Length > MaxUploadedAudioBytes)
            return BadRequest(new { message = "File âm thanh không được vượt quá 25 MB." });

        var extension = Path.GetExtension(Path.GetFileName(audio.FileName ?? string.Empty));
        var normalizedContentType = audio.ContentType?.Split(';')[0].Trim() ?? string.Empty;
        var contentTypeAllowed = AllowedAudioContentTypes.Contains(normalizedContentType);
        var extensionAllowed = !string.IsNullOrEmpty(extension) && AllowedAudioExtensions.Contains(extension);
        if (!contentTypeAllowed && !extensionAllowed)
            return BadRequest(new { message = "Định dạng âm thanh không được hỗ trợ. Chấp nhận: webm, ogg, wav, mp3, m4a/mp4." });

        // Per-user scratch file, mirroring the chat-attachment pattern in ChatController:
        // write with CreateNew under Uploads/{tenant}/{user}, always delete in finally.
        var uploadDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Uploads",
            tenantId.ToString("N"),
            userId.ToString("N"),
            "SpeechUploads");
        Directory.CreateDirectory(uploadDir);

        var scratchExtension = extensionAllowed
            ? extension.ToLowerInvariant()
            : normalizedContentType.ToLowerInvariant() switch
            {
                "audio/webm" => ".webm",
                "audio/ogg" => ".ogg",
                "audio/wav" => ".wav",
                "audio/mpeg" => ".mp3",
                "audio/mp4" => ".m4a",
                _ => ".bin"
            };
        var savedPath = Path.Combine(uploadDir, $"{Guid.NewGuid():N}{scratchExtension}");

        try
        {
            await using (var stream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await audio.CopyToAsync(stream, cancellationToken);
            }

            var result = await _mediator.Send(new TranscribeAudioCommand
            {
                AudioPath = savedPath,
                EnableVad = true
            }, cancellationToken);

            return Ok(new { text = result.Text });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LMKit.Exceptions.LicenseException
            or LMKit.Exceptions.ModelNotLoadedException
            or LMKit.Exceptions.ModelNotDownloadedException
            or LMKit.Exceptions.InvalidModelException)
        {
            _logger.LogWarning(ex, "Speech model unavailable for uploaded transcription.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Mô hình nhận dạng giọng nói hiện chưa sẵn sàng. Vui lòng thử lại sau." });
        }
        catch (Exception ex) when (ex is LMKit.Exceptions.CorruptedAudioException
            or LMKit.Exceptions.NotSupportedAudioException
            or InvalidDataException
            or FormatException
            or ArgumentException
            or EndOfStreamException)
        {
            _logger.LogWarning(ex, "Uploaded audio could not be decoded.");
            return BadRequest(new { message = "Không thể đọc file âm thanh. File có thể bị hỏng hoặc định dạng chưa được hỗ trợ." });
        }
        catch (FileNotFoundException)
        {
            return BadRequest(new { message = "Không tìm thấy file âm thanh tạm. Vui lòng thử lại." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uploaded audio transcription failed.");
            return Problem(statusCode: 500, title: "Audio transcription failed.");
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
                _logger.LogWarning(cleanupError, "Unable to delete temporary speech upload {Path}", savedPath);
            }
        }
    }

    [HttpPost("detect-language")]
    public async Task<IActionResult> DetectLanguage([FromBody] AudioLanguageDetectionRequest request)
    {
        if (string.IsNullOrEmpty(request.AudioPath))
            return BadRequest("AudioPath cannot be empty.");
        var path = ValidateOwnedPath(request.AudioPath);
        if (!path.IsAllowed) return BadRequest(path.DenialReason);

        try
        {
            var command = new DetectAudioLanguageCommand { AudioPath = path.SanitizedPath };
            var result = await _mediator.Send(command);

            return Ok(new AudioLanguageDetectionResponse 
            { 
                Language = result.Language,
                Confidence = result.Confidence
            });
        }
        catch (FileNotFoundException)
        {
            return NotFound("The requested audio file was not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio language detection failed.");
            return Problem(statusCode: 500, title: "Audio language detection failed.");
        }
    }

    [HttpGet("token")]
    public IActionResult GetLiveKitToken([FromServices] IConfiguration config, [FromQuery] string room = "omni-room")
    {
        if (string.IsNullOrWhiteSpace(room) || room.Length > 100)
            return BadRequest("Room must contain between 1 and 100 characters.");

        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var apiKey = config["LiveKit:ApiKey"];
        var apiSecret = config["LiveKit:ApiSecret"];

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            return StatusCode(500, "LiveKit is not configured");

        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(apiSecret));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var header = new System.IdentityModel.Tokens.Jwt.JwtHeader(credentials);
        var payload = new System.IdentityModel.Tokens.Jwt.JwtPayload(
            issuer: apiKey,
            audience: null,
            claims: null,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(2)
        );

        var safeRoom = System.Text.RegularExpressions.Regex.Replace(room, "[^a-zA-Z0-9_-]+", "-").Trim('-');
        if (safeRoom.Length == 0) return BadRequest("Room contains no supported characters.");
        var scopedRoom = $"{tenantId:N}-{safeRoom}";
        payload.AddClaim(new System.Security.Claims.Claim(
            System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
            userId.ToString("N")));
        
        var videoClaim = new Dictionary<string, object>
        {
            { "roomJoin", true },
            { "room", scopedRoom }
        };
        payload.Add("video", videoClaim);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(header, payload);
        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        
        return Ok(new { token = tokenHandler.WriteToken(token) });
    }

    private PathValidationResult ValidateOwnedPath(string path)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return PathValidationResult.Deny("Authenticated tenant/user identity is missing.");
        return _resources.ValidateOwnedPath(tenantId, userId, path);
    }
}
