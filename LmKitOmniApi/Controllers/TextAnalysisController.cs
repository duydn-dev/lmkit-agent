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

    [HttpPost("classify")]
    public async Task<IActionResult> ClassifyText([FromBody] ClassifyTextRequest request)
    {
        if (string.IsNullOrEmpty(request.Text) || request.Categories == null || request.Categories.Length == 0)
            return BadRequest("Text and Categories must not be empty.");

        try
        {
            var command = new ClassifyTextCommand
            {
                Text = request.Text,
                Categories = request.Categories
            };

            var result = await _mediator.Send(command);

            return Ok(new ClassifyTextResponse
            {
                Category = result.Category,
                Confidence = result.Confidence
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("detect-language")]
    public async Task<IActionResult> DetectLanguage([FromBody] TextAnalysisRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");

        try
        {
            var command = new DetectLanguageCommand { Text = request.Text };
            var result = await _mediator.Send(command);

            return Ok(new DetectLanguageResponse { Language = result.Language });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("extract-keywords")]
    public async Task<IActionResult> ExtractKeywords([FromBody] TextAnalysisRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");

        try
        {
            var command = new ExtractKeywordsCommand { Text = request.Text };
            var result = await _mediator.Send(command);

            return Ok(new ExtractKeywordsResponse
            {
                Keywords = result.Keywords,
                Confidence = result.Confidence
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("embeddings")]
    public async Task<IActionResult> GenerateEmbeddings([FromBody] TextAnalysisRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");

        try
        {
            var command = new GenerateEmbeddingsCommand { Text = request.Text };
            var result = await _mediator.Send(command);

            return Ok(new GenerateEmbeddingsResponse { Embeddings = result.Embeddings });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
