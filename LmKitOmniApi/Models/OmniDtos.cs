using System.ComponentModel.DataAnnotations;

namespace LmKitOmniApi.Models;

/// <summary>
/// Body of <c>PATCH /api/chat/sessions/{id}</c>. Wire shape: <c>{ "title": string }</c>.
/// Validation (non-empty, max 100 chars after trimming) lives in ChatController so the
/// error payload matches the codebase's Vietnamese <c>{ message }</c> convention.
/// </summary>
public class RenameChatSessionRequest
{
    public string? Title { get; set; }
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
    public bool EnableVad { get; set; } = false;
}

public class AudioLanguageDetectionRequest
{
    [Required]
    public string AudioPath { get; set; } = string.Empty;
}

public class AudioLanguageDetectionResponse
{
    public string Language { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

public class ClassifyTextRequest
{
    [Required]
    public string Text { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
}

public class ClassifyTextResponse
{
    public string Category { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

public class DetectLanguageResponse
{
    public string Language { get; set; } = string.Empty;
}

public class ExtractKeywordsResponse
{
    public List<string> Keywords { get; set; } = new();
    public float Confidence { get; set; }
}

public class GenerateEmbeddingsResponse
{
    public float[] Embeddings { get; set; } = Array.Empty<float>();
}

public class RemoveBackgroundRequest
{
    [Required]
    public string ImagePath { get; set; } = string.Empty;
}

public class RemoveBackgroundResponse
{
    public string Base64Image { get; set; } = string.Empty;
}

public class ClassifyImageRequest
{
    [Required]
    public string ImagePath { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
}

public class ClassifyImageResponse
{
    public string Category { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

public class ExtractTextFromImageRequest
{
    [Required]
    public string ImagePath { get; set; } = string.Empty;
    public bool IncludeCoordinates { get; set; } = false;
}

public class ExtractTextFromImageResponse
{
    public string Text { get; set; } = string.Empty;
    public List<TextRegion> Regions { get; set; } = new();
}

public class TextRegion
{
    public string Text { get; set; } = string.Empty;
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class ExtractDocumentDataRequest
{
    [Required]
    public string DocumentPath { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
}

public class ExtractDocumentDataResponse
{
    public string JsonData { get; set; } = string.Empty;
}

public class ContentCreationPipelineRequest
{
    [Required]
    public string Topic { get; set; } = string.Empty;
}
