using System.ComponentModel.DataAnnotations;

namespace LmKitOmniApi.Models;

public class ChatCompletionRequest
{
    [Required]
    public List<ChatMessage> Messages { get; set; } = new();
    public string? Model { get; set; }
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 2048;
}

public class ChatMessage
{
    public string Role { get; set; } = "user"; // "system", "user", "assistant"
    public string Content { get; set; } = string.Empty;
}

public class ChatCompletionResponse
{
    public string Text { get; set; } = string.Empty;
    public int GeneratedTokens { get; set; }
    public double TokenGenerationRate { get; set; }
    public string StopReason { get; set; } = string.Empty;
}

public class DocumentConversionRequest
{
    [Required]
    public string FilePath { get; set; } = string.Empty; // In a real API this would be IFormFile, using path for local MVP
    public string Strategy { get; set; } = "Hybrid"; // "Hybrid", "TextExtraction", "VlmOcr"
}

public class DocumentConversionResponse
{
    public string Markdown { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public class TextAnalysisRequest
{
    [Required]
    public string Text { get; set; } = string.Empty;
}

public class TextAnalysisResponse
{
    public string Sentiment { get; set; } = string.Empty;
    public double SentimentConfidence { get; set; }
    public List<string> ExtractedEntities { get; set; } = new();
    public string RedactedText { get; set; } = string.Empty;
}

public class VisionAnalysisRequest
{
    [Required]
    public string ImagePath { get; set; } = string.Empty;
    public string Prompt { get; set; } = "Describe this image.";
}

public class SpeechTranscriptionRequest
{
    [Required]
    public string AudioPath { get; set; } = string.Empty;
}
