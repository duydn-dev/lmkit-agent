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
}
