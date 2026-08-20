using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("ai-agent")]
public class TextAnalysisController : ControllerBase
{
    private const int MaximumTextLength = 50_000;
    private readonly IMediator _mediator;

    public TextAnalysisController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeText([FromBody] TextAnalysisRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");
        if (request.Text.Length > MaximumTextLength) return BadRequest($"Text cannot exceed {MaximumTextLength} characters.");

        try
        {
            var command = new AnalyzeTextCommand
            {
                Text = request.Text
            };

            var result = await _mediator.Send(command, ct);

            return Ok(new TextAnalysisResponse
            {
                Sentiment = result.Sentiment,
                SentimentConfidence = result.SentimentConfidence,
                ExtractedEntities = result.ExtractedEntities,
                RedactedText = result.RedactedText
            });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Text analysis failed.");
        }
    }

    [HttpPost("classify")]
    public async Task<IActionResult> ClassifyText([FromBody] ClassifyTextRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Text) || request.Categories == null || request.Categories.Length == 0)
            return BadRequest("Text and Categories must not be empty.");
        if (request.Text.Length > MaximumTextLength
            || request.Categories.Length > 100
            || request.Categories.Any(category => string.IsNullOrWhiteSpace(category) || category.Length > 100))
            return BadRequest("Text or category limits were exceeded.");

        try
        {
            var command = new ClassifyTextCommand
            {
                Text = request.Text,
                Categories = request.Categories
            };

            var result = await _mediator.Send(command, ct);

            return Ok(new ClassifyTextResponse
            {
                Category = result.Category,
                Confidence = result.Confidence
            });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Text classification failed.");
        }
    }

    [HttpPost("detect-language")]
    public async Task<IActionResult> DetectLanguage([FromBody] TextAnalysisRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");
        if (request.Text.Length > MaximumTextLength) return BadRequest($"Text cannot exceed {MaximumTextLength} characters.");

        try
        {
            var command = new DetectLanguageCommand { Text = request.Text };
            var result = await _mediator.Send(command, ct);

            return Ok(new DetectLanguageResponse { Language = result.Language });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Language detection failed.");
        }
    }

    [HttpPost("extract-keywords")]
    public async Task<IActionResult> ExtractKeywords([FromBody] TextAnalysisRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");
        if (request.Text.Length > MaximumTextLength) return BadRequest($"Text cannot exceed {MaximumTextLength} characters.");

        try
        {
            var command = new ExtractKeywordsCommand { Text = request.Text };
            var result = await _mediator.Send(command, ct);

            return Ok(new ExtractKeywordsResponse
            {
                Keywords = result.Keywords,
                Confidence = result.Confidence
            });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Keyword extraction failed.");
        }
    }

    [HttpPost("embeddings")]
    public async Task<IActionResult> GenerateEmbeddings([FromBody] TextAnalysisRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text cannot be empty.");
        if (request.Text.Length > MaximumTextLength) return BadRequest($"Text cannot exceed {MaximumTextLength} characters.");

        try
        {
            var command = new GenerateEmbeddingsCommand { Text = request.Text };
            var result = await _mediator.Send(command, ct);

            return Ok(new GenerateEmbeddingsResponse { Embeddings = result.Embeddings });
        }
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Embedding generation failed.");
        }
    }
}
