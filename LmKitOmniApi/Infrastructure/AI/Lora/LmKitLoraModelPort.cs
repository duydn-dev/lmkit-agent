using LMKit.Finetuning;
using LMKit.Model;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Default <see cref="ILoraModelPort"/> — the ONLY place LM-Kit.NET's LoRA API is
/// touched. Applying/removing an adapter mutates the shared chat model, so callers must
/// already hold the chat inference lease (see <c>LmModelManager.AcquireChatInferenceAsync</c>)
/// before applying, and remove before releasing it. This type requires a real native
/// model and is therefore exercised live, not in CI.
/// </summary>
public sealed class LmKitLoraModelPort : ILoraModelPort
{
    private readonly ILogger<LmKitLoraModelPort> _logger;

    public LmKitLoraModelPort(ILogger<LmKitLoraModelPort> logger) => _logger = logger;

    public IDisposable Apply(LM model, string adapterPath, float scale)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterPath);

        // Identify EXACTLY the adapter this call adds by diffing the applied set around
        // ApplyLoraAdapter (reference identity), so removal targets our adapter even if
        // an identically-pathed one is somehow present. Fall back to a path match.
        var before = new HashSet<LoraAdapter>(model.Adapters);
        model.ApplyLoraAdapter(adapterPath, scale);

        var applied = model.Adapters.FirstOrDefault(a => !before.Contains(a))
            ?? model.Adapters.LastOrDefault(a => PathMatches(a.Path, adapterPath));

        if (applied is null)
        {
            _logger.LogWarning(
                "LoRA adapter at {AdapterPath} was applied but could not be located in Adapters for later removal.",
                adapterPath);
        }

        return new RemovalHandle(model, applied, _logger);
    }

    public IReadOnlyList<string> ListApplied(LM model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Adapters
            .Select(a => a.Path ?? a.Identifier)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    public bool ValidateFormat(string adapterPath)
    {
        if (string.IsNullOrWhiteSpace(adapterPath)) return false;
        try
        {
            // throwException: false → a malformed adapter returns false rather than throwing.
            return LoraAdapterSource.ValidateFormat(adapterPath, throwException: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoRA adapter format validation threw for {AdapterPath}; treating as invalid.", adapterPath);
            return false;
        }
    }

    private static bool PathMatches(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the captured adapter on Dispose. Idempotent; never throws.</summary>
    private sealed class RemovalHandle : IDisposable
    {
        private readonly LM _model;
        private readonly ILogger _logger;
        private LoraAdapter? _adapter;

        public RemovalHandle(LM model, LoraAdapter? adapter, ILogger logger)
        {
            _model = model;
            _adapter = adapter;
            _logger = logger;
        }

        public void Dispose()
        {
            var adapter = Interlocked.Exchange(ref _adapter, null);
            if (adapter is null) return;
            try
            {
                if (!_model.RemoveLoraAdapter(adapter))
                    _logger.LogWarning("RemoveLoraAdapter reported the adapter {Identifier} was not applied.", adapter.Identifier);
            }
            catch (Exception ex)
            {
                // Removal failure must never mask the wrapped inference's own outcome.
                _logger.LogError(ex, "Failed to remove LoRA adapter {Identifier} after inference.", adapter.Identifier);
            }
        }
    }
}
