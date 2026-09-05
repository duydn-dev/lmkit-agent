using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Default <see cref="IGroundingTraceRecorder"/>: appends each vetted sample as ONE JSON
/// line to a tenant-scoped <c>samples.jsonl</c> under the configured dataset root. Purely
/// file I/O over plain <see cref="GroundingSample"/> DTOs — no model, no database — so it is
/// fully CI-testable (record→read roundtrip, disabled→no-op, cross-tenant isolation).
///
/// Security: the on-disk path is entirely server-controlled — the dataset root from options
/// plus a per-tenant subdirectory named by the tenant <see cref="Guid"/> ("N" format). A
/// client-supplied path is NEVER used, exactly like the LoRA adapter storage. Registered as
/// a singleton; a private semaphore serializes concurrent appends so JSON lines never
/// interleave.
/// </summary>
public sealed class FileGroundingTraceRecorder : IGroundingTraceRecorder
{
    private const string DatasetFileName = "samples.jsonl";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact single-line JSON: exactly one sample per JSONL row.
        WriteIndented = false,
    };

    private readonly GroundingTrainingOptions _options;
    private readonly ILogger<FileGroundingTraceRecorder> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FileGroundingTraceRecorder(
        IOptions<GroundingTrainingOptions> options,
        ILogger<FileGroundingTraceRecorder> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public async Task RecordAsync(GroundingSample sample, CancellationToken ct = default)
    {
        if (!Enabled) return; // off by default → capture nothing
        ArgumentNullException.ThrowIfNull(sample);

        var path = DatasetFilePath(sample.TenantId);
        // Serialize outside the lock; the JSON has no embedded newlines (WriteIndented=false),
        // so one sample maps to exactly one line.
        var line = JsonSerializer.Serialize(sample, SerializerOptions) + "\n";

        await _writeGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<GroundingSample>> ReadAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!Enabled) return Array.Empty<GroundingSample>();

        var path = DatasetFilePath(tenantId);
        if (!File.Exists(path)) return Array.Empty<GroundingSample>();

        var samples = new List<GroundingSample>();
        var lines = await File.ReadAllLinesAsync(path, ct);
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var sample = JsonSerializer.Deserialize<GroundingSample>(raw, SerializerOptions);
                if (sample is not null) samples.Add(sample);
            }
            catch (JsonException ex)
            {
                // A single corrupt line must not sink the whole read; skip it and keep going.
                _logger.LogWarning(ex, "Skipping a malformed grounding-sample line for tenant {TenantId}.", tenantId);
            }
        }
        return samples;
    }

    public async Task<int> CountAsync(Guid tenantId, CancellationToken ct = default)
        => (await ReadAsync(tenantId, ct)).Count;

    /// <summary>Server-controlled, tenant-scoped dataset file path (never a client path).</summary>
    private string DatasetFilePath(Guid tenantId)
        => Path.Combine(_options.ResolveDatasetRoot(), tenantId.ToString("N"), DatasetFileName);
}
