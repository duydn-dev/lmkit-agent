using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Models;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SpeechController : ControllerBase
{
    private readonly IMediator _mediator;

    public SpeechController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("transcribe")]
    public async Task<IActionResult> TranscribeAudio([FromBody] SpeechTranscriptionRequest request)
    {
        try
        {
            var command = new TranscribeAudioCommand
            {
                AudioPath = request.AudioPath,
                EnableVad = request.EnableVad
            };

            var result = await _mediator.Send(command);

            return Ok(new
            {
                Text = result.Text,
                Duration = result.DurationSeconds
            });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("detect-language")]
    public async Task<IActionResult> DetectLanguage([FromBody] AudioLanguageDetectionRequest request)
    {
        if (string.IsNullOrEmpty(request.AudioPath))
            return BadRequest("AudioPath cannot be empty.");

        try
        {
            var command = new DetectAudioLanguageCommand { AudioPath = request.AudioPath };
            var result = await _mediator.Send(command);

            return Ok(new AudioLanguageDetectionResponse 
            { 
                Language = result.Language,
                Confidence = result.Confidence
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

    [HttpGet("token")]
    public IActionResult GetLiveKitToken([FromServices] IConfiguration config, [FromQuery] string room = "omni-room", [FromQuery] string participant = "user-123")
    {
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

        payload.AddClaim(new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, participant));
        
        var videoClaim = new Dictionary<string, object>
        {
            { "roomJoin", true },
            { "room", room }
        };
        payload.Add("video", videoClaim);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(header, payload);
        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        
        return Ok(new { token = tokenHandler.WriteToken(token) });
    }
}
