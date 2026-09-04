namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Shared, model-free input guards for the document services. Every check runs
/// BEFORE any LM-Kit call so a disabled feature, an over-limit upload, a spoofed
/// extension or an abusive term list is refused up front. Magic-byte sniffing means
/// a caller cannot smuggle a non-PDF into the PDF engine (or a non-package into the
/// OpenXML engine) by renaming it.
/// </summary>
internal static class DocumentInputValidator
{
    // "%PDF" — every PDF begins with this signature.
    private static readonly byte[] PdfMagic = "%PDF"u8.ToArray();

    // "PK\x03\x04" — the local-file-header magic shared by ZIP and every OpenXML
    // package (.docx / .xlsx / .pptx are ZIP containers).
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    /// <summary>OpenXML document extensions the Office redactor accepts.</summary>
    private static readonly HashSet<string> OpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx",
        ".xlsx", ".xlsm", ".xltx",
        ".pptx", ".pptm", ".potx",
    };

    /// <summary>Throws <see cref="DocumentToolsDisabledException"/> when the feature is off.</summary>
    public static void EnsureEnabled(bool enabled)
    {
        if (!enabled)
            throw new DocumentToolsDisabledException("The document tools feature is disabled.");
    }

    /// <summary>Throws when <paramref name="data"/> is empty or exceeds the input cap.</summary>
    public static void EnsureWithinInputLimit(byte[] data, long maxInputBytes)
    {
        if (data is null || data.Length == 0)
            throw new DocumentValidationException("The uploaded document is empty.");
        if (data.LongLength > maxInputBytes)
            throw new DocumentValidationException($"The document exceeds the {maxInputBytes}-byte input limit.");
    }

    /// <summary>Throws when the produced document exceeds the output cap.</summary>
    public static void EnsureWithinOutputLimit(byte[] data, long maxOutputBytes)
    {
        if (data is not null && data.LongLength > maxOutputBytes)
            throw new DocumentValidationException($"The produced document exceeds the {maxOutputBytes}-byte output limit.");
    }

    /// <summary>Throws when the bytes are not a PDF (missing the <c>%PDF</c> signature).</summary>
    public static void EnsurePdf(byte[] data)
    {
        if (!StartsWith(data, PdfMagic))
            throw new DocumentValidationException("The uploaded file is not a valid PDF document.");
    }

    /// <summary>
    /// Throws when the bytes are not an OpenXML package (missing the ZIP
    /// <c>PK\x03\x04</c> signature) or the extension is not a supported OpenXML type.
    /// Returns the normalized, lower-cased extension for the LM-Kit call.
    /// </summary>
    public static string EnsureOpenXml(byte[] data, string? extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;
        if (!OpenXmlExtensions.Contains(ext))
            throw new DocumentValidationException(
                $"Unsupported Office document type '{ext}'. Supported: {string.Join(", ", OpenXmlExtensions)}.");
        if (!StartsWith(data, ZipMagic))
            throw new DocumentValidationException("The uploaded file is not a valid OpenXML (Office) document.");
        return ext;
    }

    /// <summary>
    /// Validates the redaction term list: at least one non-blank term, at most
    /// <paramref name="maxSearchTerms"/>. Returns the trimmed, non-blank terms.
    /// </summary>
    public static List<string> EnsureSearchTerms(IEnumerable<string> searchTerms, int maxSearchTerms)
    {
        var terms = (searchTerms ?? Enumerable.Empty<string>())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .ToList();

        if (terms.Count == 0)
            throw new DocumentValidationException("At least one non-empty search term is required.");
        if (terms.Count > maxSearchTerms)
            throw new DocumentValidationException($"Too many search terms (maximum {maxSearchTerms}).");
        return terms;
    }

    private static bool StartsWith(byte[] data, byte[] prefix) =>
        data is not null && data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
