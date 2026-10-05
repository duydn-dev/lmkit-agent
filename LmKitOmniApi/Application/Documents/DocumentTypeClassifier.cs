namespace LmKitOmniApi.Application.Documents;

/// <summary>
/// Maps a file name to the coarse "doc type" used for metadata-aware retrieval
/// (payload field <see cref="LmKitOmniApi.Application.Abstractions.VectorPayloadFields.DocType"/>).
/// Shared by the vectorization worker and the ad-hoc ingest path so both classify
/// identically, and by the query-filter extractor so a user asking for "file pdf"
/// filters on the same value the writer stored.
/// </summary>
public static class DocumentTypeClassifier
{
    public static string FromFileName(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".doc" or ".docx" => "word",
            ".xls" or ".xlsx" or ".csv" => "excel",
            ".ppt" or ".pptx" => "powerpoint",
            ".txt" => "text",
            ".md" or ".markdown" => "markdown",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "image",
            _ => "other"
        };
    }
}
