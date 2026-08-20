using MediatR;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Models;
using Microsoft.AspNetCore.Authorization;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Text analysis failed.");
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Text classification failed.");
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Language detection failed.");
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Keyword extraction failed.");
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
        catch (Exception)
        {
            return Problem(statusCode: 500, title: "Embedding generation failed.");
        }
    }
}
