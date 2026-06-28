using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Models;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TextAnalysisController : ControllerBase
{
    private readonly IMediator _mediator;

    public TextAnalysisController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeText([FromBody] TextAnalysisRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");

        try
        {
            var command = new AnalyzeTextCommand
            {
                Text = request.Text
            };

            var result = await _mediator.Send(command);

            return Ok(new TextAnalysisResponse
            {
                Sentiment = result.Sentiment,
                SentimentConfidence = result.SentimentConfidence,
                ExtractedEntities = result.ExtractedEntities,
                RedactedText = result.RedactedText
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
