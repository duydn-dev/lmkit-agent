namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Thrown by a document service when it is invoked while the feature is disabled
/// (DocumentTools:Enabled = false). The controller pre-checks <c>IsEnabled</c> and
/// returns 501 (feature off); this is the defense-in-depth guarantee for any caller
/// that skipped that check.
/// </summary>
public sealed class DocumentToolsDisabledException : Exception
{
    public DocumentToolsDisabledException(string message) : base(message) { }
}

/// <summary>
/// Thrown when an input fails validation BEFORE LM-Kit is touched: over the size
/// cap, wrong magic bytes (not a PDF / not an OpenXML package), an unsupported
/// extension, too many search terms, an empty term list, or a produced document that
/// exceeds the output cap. The controller maps this to 400 and surfaces the message
/// (it carries no sensitive detail).
/// </summary>
public sealed class DocumentValidationException : Exception
{
    public DocumentValidationException(string message) : base(message) { }
}
