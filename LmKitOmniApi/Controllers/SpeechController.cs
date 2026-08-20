using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Models;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SpeechController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;

    public SpeechController(IMediator mediator, UserResourceAccessService resources)
    {
        _mediator = mediator;
        _resources = resources;
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Audio transcription failed.");
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Audio language detection failed.");
        }
    }

    [HttpGet("token")]
    public IActionResult GetLiveKitToken([FromServices] IConfiguration config, [FromQuery] string room = "omni-room")
    {
        if (string.IsNullOrWhiteSpace(room) || room.Length > 100)
            return BadRequest("Room must contain between 1 and 100 characters.");

        if (!Guid.TryParse(User.FindFirst("TenantId")?.Value, out var tenantId)
            || !Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
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
        if (!Guid.TryParse(User.FindFirst("TenantId")?.Value, out var tenantId)
            || !Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return PathValidationResult.Deny("Authenticated tenant/user identity is missing.");
        return _resources.ValidateOwnedPath(tenantId, userId, path);
    }
}
