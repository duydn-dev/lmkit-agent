using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// The disposable returned by <see cref="ILoraAdapterService.BeginApplyForAgent"/>. It
/// owns the removal handle from <see cref="ILoraModelPort.Apply"/>; disposing it removes
/// the adapter from the shared model. Disposal is idempotent and never throws, so it is
/// safe in a <c>using</c>/finally around inference that may itself throw — the whole
/// point being that the adapter is ALWAYS removed before the inference lease is released.
/// </summary>
public sealed class LoraApplyScope : IDisposable
{
    private readonly ILogger? _logger;
    private readonly Guid _adapterId;
    private IDisposable? _removal;

    internal LoraApplyScope(IDisposable removal, Guid adapterId, ILogger? logger)
    {
        _removal = removal;
        _adapterId = adapterId;
        _logger = logger;
    }

    public void Dispose()
    {
        var removal = Interlocked.Exchange(ref _removal, null);
        if (removal is null) return;
        try
        {
            removal.Dispose();
        }
        catch (Exception ex)
        {
            // Defensive: the port's handle already swallows removal failures, but a scope
            // Dispose must never surface an exception into a caller's finally.
            _logger?.LogError(ex, "Failed to remove LoRA adapter {AdapterId} on scope dispose.", _adapterId);
        }
    }
}
