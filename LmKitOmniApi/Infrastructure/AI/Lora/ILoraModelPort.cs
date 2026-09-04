using LMKit.Model;

namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Thin seam over the LM-Kit.NET LoRA API on a loaded <see cref="LM"/> instance.
/// ALL LM-Kit LoRA calls (<c>ApplyLoraAdapter</c> / <c>RemoveLoraAdapter</c> /
/// <c>Adapters</c> and <c>LoraAdapterSource.ValidateFormat</c>) live behind this
/// interface, so <see cref="LoraAdapterService"/> and everything above it is unit
/// testable with a fake port and no native model.
/// </summary>
public interface ILoraModelPort
{
    /// <summary>
    /// Applies the adapter file at <paramref name="adapterPath"/> to
    /// <paramref name="model"/> at <paramref name="scale"/> and returns a disposable
    /// whose <see cref="IDisposable.Dispose"/> removes exactly that adapter again. The
    /// caller MUST dispose it (via <c>using</c>/finally) so removal runs even if the
    /// wrapped inference throws.
    /// </summary>
    IDisposable Apply(LM model, string adapterPath, float scale);

    /// <summary>Paths of the adapters currently applied to <paramref name="model"/>.</summary>
    IReadOnlyList<string> ListApplied(LM model);

    /// <summary>
    /// True when the file at <paramref name="adapterPath"/> is a valid LoRA adapter
    /// (delegates to <c>LoraAdapterSource.ValidateFormat</c>). Never throws — a malformed
    /// or unreadable file returns false.
    /// </summary>
    bool ValidateFormat(string adapterPath);
}
