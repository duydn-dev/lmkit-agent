using LMKit.Document.Conversion;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Test-only probe for the LM-Kit.NET native document engine. The document tools are
/// PURE document APIs (no model, no network) and are meant to run for real in CI, so
/// the real-PDF tests exercise the actual engine. This probe exists ONLY so that a
/// test host on which the native library genuinely cannot load degrades to a skipped
/// (yellow) test instead of a hard failure — it returns false ONLY for native
/// load-time failures (missing/incompatible native binary), never for logic errors.
/// </summary>
internal static class NativeDocumentEngine
{
    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>True when a minimal Markdown→PDF conversion succeeds, i.e. the native PDF engine is loadable.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>Produces a real PDF from Markdown (used to build fixtures for the real-API tests).</summary>
    public static byte[] PdfFromMarkdown(string markdown) => MarkdownToPdf.ConvertToBytes(markdown);

    private static bool Probe()
    {
        try
        {
            var bytes = MarkdownToPdf.ConvertToBytes("# probe");
            return bytes is { Length: > 0 };
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            return false;
        }
    }

    private static bool IsNativeLoadFailure(Exception ex) =>
        ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or PlatformNotSupportedException
        || (ex.InnerException is not null && IsNativeLoadFailure(ex.InnerException));
}
