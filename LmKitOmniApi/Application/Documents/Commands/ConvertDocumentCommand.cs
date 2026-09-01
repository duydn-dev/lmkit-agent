using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

/// <summary>
/// Converts a caller-owned document (path already validated by the controller
/// via UserResourceAccessService) to Markdown, optionally using the vision
/// model for OCR depending on the requested strategy.
/// </summary>
public class ConvertDocumentCommand : IRequest<ConvertDocumentResult>
{
    /// <summary>Sanitized, ownership-validated absolute path of the document.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>"Hybrid", "TextExtraction" or "VlmOcr".</summary>
    public string Strategy { get; set; } = "Hybrid";
}

public class ConvertDocumentResult
{
    public string Markdown { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public TimeSpan Elapsed { get; set; }
}
