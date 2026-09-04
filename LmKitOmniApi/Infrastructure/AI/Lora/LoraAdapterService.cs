using LMKit.Model;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Default <see cref="ILoraAdapterService"/>. Pure policy/plumbing over
/// <see cref="ILoraModelPort"/> (the only LM-Kit seam) and <see cref="HermesDbContext"/>,
/// so it is hermetically testable with a fake port and an in-memory SQLite context.
///
/// Security: adapter files are Admin-uploaded only; they are stored under a
/// server-controlled, per-tenant subdirectory of the configured storage root — the
/// upload's own file name is NEVER used for the path — validated for format before the
/// row is persisted, and size-capped while streaming to disk.
/// </summary>
public sealed class LoraAdapterService : ILoraAdapterService
{
    private const int CopyBufferBytes = 64 * 1024;
    private const string AdapterFileExtension = ".gguf";

    private readonly HermesDbContext _db;
    private readonly ILoraModelPort _port;
    private readonly LoraOptions _options;
    private readonly ILogger<LoraAdapterService> _logger;

    public LoraAdapterService(
        HermesDbContext db,
        ILoraModelPort port,
        IOptions<LoraOptions> options,
        ILogger<LoraAdapterService> logger)
    {
        _db = db;
        _port = port;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>Effective storage root; defaults to &lt;current-dir&gt;/App_Data/lora when unset.</summary>
    private string StorageRoot => string.IsNullOrWhiteSpace(_options.AdapterStoragePath)
        ? Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "lora")
        : _options.AdapterStoragePath;

    public async Task<LoraAdapterRegistration> RegisterAsync(
        Guid tenantId,
        string name,
        string? description,
        Stream content,
        long contentLength,
        float? scale,
        string? targetModelId,
        CancellationToken ct = default)
    {
        if (!Enabled)
            throw new LoraFeatureDisabledException();

        ArgumentNullException.ThrowIfNull(content);

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
            throw new LoraAdapterValidationException("Tên adapter là bắt buộc.");

        // Cheap early reject when the declared length already exceeds the cap; the
        // streaming copy below enforces the true cap regardless of a lying length.
        if (contentLength > _options.MaxAdapterBytes)
            throw new LoraAdapterValidationException(
                $"Tệp adapter vượt quá giới hạn {_options.MaxAdapterBytes} byte.");

        // Duplicate-name guard (the unique index is the race-safe backstop below).
        var nameTaken = await _db.LoraAdapterRegistrations
            .AnyAsync(a => a.TenantId == tenantId && a.Name == trimmedName, ct);
        if (nameTaken)
            throw new LoraAdapterValidationException($"Đã tồn tại adapter tên '{trimmedName}'.");

        var id = Guid.NewGuid();
        var tenantDir = Path.Combine(StorageRoot, tenantId.ToString("N"));
        Directory.CreateDirectory(tenantDir);
        var finalPath = Path.Combine(tenantDir, id.ToString("N") + AdapterFileExtension);
        var tempPath = finalPath + $".{Guid.NewGuid():N}.tmp";

        long written;
        try
        {
            written = await StreamToFileWithCapAsync(content, tempPath, ct);

            if (!_port.ValidateFormat(tempPath))
                throw new LoraAdapterValidationException("Tệp không phải là adapter LoRA hợp lệ.");

            File.Move(tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var effectiveScale = Math.Clamp(scale ?? _options.DefaultScale, _options.MinScale, _options.MaxScale);
        var registration = new LoraAdapterRegistration
        {
            Id = id,
            TenantId = tenantId,
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            FilePath = finalPath,
            Scale = effectiveScale,
            TargetModelId = string.IsNullOrWhiteSpace(targetModelId) ? null : targetModelId.Trim(),
            FileSizeBytes = written,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.LoraAdapterRegistrations.Add(registration);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the (TenantId, Name) unique-index race, or another constraint tripped.
            // The row was never committed; drop the orphaned file so nothing leaks.
            TryDelete(finalPath);
            throw new LoraAdapterValidationException($"Đã tồn tại adapter tên '{trimmedName}'.");
        }

        _logger.LogInformation(
            "Registered LoRA adapter {AdapterId} ('{Name}', {Bytes} bytes) for tenant {TenantId}.",
            registration.Id, registration.Name, registration.FileSizeBytes, tenantId);
        return registration;
    }

    public async Task<IReadOnlyList<LoraAdapterRegistration>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        // A disabled feature exposes an empty list rather than an error (mutations 501).
        if (!Enabled) return Array.Empty<LoraAdapterRegistration>();

        return await _db.LoraAdapterRegistrations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<LoraAdapterRegistration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        return await _db.LoraAdapterRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var registration = await _db.LoraAdapterRegistrations
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (registration is null) return false;

        _db.LoraAdapterRegistrations.Remove(registration);
        await _db.SaveChangesAsync(ct);

        // File removal is best-effort AFTER the row is gone: a leftover file is harmless
        // (nothing references it), whereas removing the file first then failing the DB
        // delete would leave a row pointing at a missing file.
        TryDelete(registration.FilePath);

        _logger.LogInformation("Deleted LoRA adapter {AdapterId} for tenant {TenantId}.", id, tenantId);
        return true;
    }

    public async Task<LoraAdapterRegistration?> SetActiveAsync(Guid tenantId, Guid id, bool isActive, CancellationToken ct = default)
        => await UpdateAsync(tenantId, id, name: null, scale: null, isActive: isActive, ct);

    public async Task<LoraAdapterRegistration?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        float? scale,
        bool? isActive,
        CancellationToken ct = default)
    {
        var registration = await _db.LoraAdapterRegistrations
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (registration is null) return null;

        if (name is not null)
        {
            var trimmedName = name.Trim();
            if (trimmedName.Length == 0)
                throw new LoraAdapterValidationException("Tên adapter là bắt buộc.");
            if (!string.Equals(trimmedName, registration.Name, StringComparison.Ordinal))
            {
                var nameTaken = await _db.LoraAdapterRegistrations
                    .AnyAsync(a => a.TenantId == tenantId && a.Name == trimmedName && a.Id != id, ct);
                if (nameTaken)
                    throw new LoraAdapterValidationException($"Đã tồn tại adapter tên '{trimmedName}'.");
                registration.Name = trimmedName;
            }
        }

        if (scale is not null)
            registration.Scale = Math.Clamp(scale.Value, _options.MinScale, _options.MaxScale);

        if (isActive is not null)
            registration.IsActive = isActive.Value;

        registration.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return registration;
    }

    public LoraApplyScope? BeginApplyForAgent(LM model, Guid tenantId, Guid? loraAdapterId, CancellationToken ct = default)
    {
        if (!Enabled || loraAdapterId is not Guid adapterId)
            return null;

        // Synchronous single-row indexed lookup: the caller already holds the chat
        // inference lease and applies the adapter inline before running the model.
        var registration = _db.LoraAdapterRegistrations
            .AsNoTracking()
            .FirstOrDefault(a => a.Id == adapterId && a.TenantId == tenantId);

        if (registration is null || !registration.IsActive)
            return null;

        if (string.IsNullOrWhiteSpace(registration.FilePath) || !File.Exists(registration.FilePath))
        {
            _logger.LogWarning(
                "LoRA adapter {AdapterId} for tenant {TenantId} is registered but its file is missing at {FilePath}; skipping.",
                adapterId, tenantId, registration.FilePath);
            return null;
        }

        try
        {
            var removal = _port.Apply(model, registration.FilePath, registration.Scale);
            _logger.LogDebug("Applied LoRA adapter {AdapterId} at scale {Scale}.", adapterId, registration.Scale);
            return new LoraApplyScope(removal, adapterId, _logger);
        }
        catch (Exception ex)
        {
            // A failed apply must not break the request; run without the adapter.
            _logger.LogError(ex, "Failed to apply LoRA adapter {AdapterId}; continuing without it.", adapterId);
            return null;
        }
    }

    private async Task<long> StreamToFileWithCapAsync(Stream content, string destinationPath, CancellationToken ct)
    {
        await using var fileStream = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);

        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > _options.MaxAdapterBytes)
                throw new LoraAdapterValidationException(
                    $"Tệp adapter vượt quá giới hạn {_options.MaxAdapterBytes} byte.");
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await fileStream.FlushAsync(ct);
        return total;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete LoRA adapter file at {FilePath}.", path);
        }
    }
}
