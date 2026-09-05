using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>Raised when a requested database operation is refused for safety (egress, or a non-read-only statement).</summary>
public sealed class DatabaseOperationRefusedException : Exception
{
    public DatabaseOperationRefusedException(string message) : base(message) { }
}

/// <summary>
/// Orchestrates external-database operations across engines with the safety layers
/// applied centrally: resolve provider → SSRF egress vet the host → (for queries)
/// classify the statement and REFUSE anything not provably read-only → execute
/// read-only. This is the single choke point every phase (test/index/query) goes
/// through, so a caller can never bypass egress or run an unclassified statement.
/// </summary>
public sealed class ExternalDatabaseService
{
    private readonly IReadOnlyDictionary<DbProvider, IExternalDatabaseProvider> _providers;
    private readonly DbEgressValidator _egress;
    private readonly DatabaseAgentOptions _options;

    public ExternalDatabaseService(
        IEnumerable<IExternalDatabaseProvider> providers,
        DbEgressValidator egress,
        IOptions<DatabaseAgentOptions> options)
    {
        _providers = providers.ToDictionary(p => p.Provider);
        _egress = egress;
        _options = options.Value;
    }

    public bool TryParseProvider(string? name, out DbProvider provider) =>
        Enum.TryParse(name, ignoreCase: true, out provider) && _providers.ContainsKey(provider);

    /// <summary>Egress-vets then opens a probe connection; returns the egress denial reason, or null on success. Throws on connect failure.</summary>
    public async Task<string?> TestConnectionAsync(DbProvider provider, string connectionString, CancellationToken ct)
    {
        var impl = _providers[provider];
        var egress = await VetAsync(impl, connectionString, ct);
        if (egress is not null) return egress;
        await impl.TestConnectionAsync(connectionString, _options.QueryTimeoutSeconds, ct);
        return null;
    }

    /// <summary>Introspects the schema after an egress check (Phase 1 indexing).</summary>
    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(DbProvider provider, string connectionString, CancellationToken ct)
    {
        var impl = _providers[provider];
        var egress = await VetAsync(impl, connectionString, ct);
        if (egress is not null) throw new DatabaseOperationRefusedException(egress);
        return await impl.IntrospectAsync(connectionString, _options.QueryTimeoutSeconds, ct);
    }

    /// <summary>
    /// Runs a read-only query. Classifies the statement first and REFUSES anything
    /// that is not provably read-only (a write or an unclassifiable statement),
    /// independent of the caller — so this method can never mutate data.
    /// </summary>
    public async Task<DbQueryResult> QueryReadOnlyAsync(DbProvider provider, string connectionString, string sql, CancellationToken ct)
    {
        var classification = SqlStatementClassifier.Classify(sql);
        if (classification.Kind != SqlStatementKind.ReadOnly)
            throw new DatabaseOperationRefusedException($"Chỉ chạy truy vấn chỉ-đọc. {classification.Reason}");

        var impl = _providers[provider];
        var egress = await VetAsync(impl, connectionString, ct);
        if (egress is not null) throw new DatabaseOperationRefusedException(egress);

        return await impl.ExecuteReadOnlyAsync(connectionString, sql, _options.MaxRows, _options.QueryTimeoutSeconds, ct);
    }

    /// <summary>
    /// Executes an APPROVED write with the mandatory safety sequence, centrally:
    /// classify (must be a write, else refuse) → resolve the single target table
    /// (else refuse) → egress vet → detect cascade/trigger side effects that would
    /// reach OTHER tables the single-table backup does NOT cover (if any, refuse) →
    /// BACK UP the table (if backup throws, the write never runs) → execute. Returns a
    /// human summary incl. the backup name.
    ///
    /// SCOPE LIMITATION (made explicit rather than hidden): <see
    /// cref="IExternalDatabaseProvider.BackupTableAsync"/> snapshots ONLY the directly
    /// targeted table. A write can still change other tables via ON DELETE/UPDATE
    /// CASCADE (or SET NULL/SET DEFAULT) foreign keys or triggers, which that backup
    /// would not restore. So when such side effects are detected the write is REFUSED —
    /// a false sense of recoverability is worse than declining the operation.
    /// </summary>
    public async Task<string> ExecuteApprovedWriteAsync(DbProvider provider, string connectionString, string sql, CancellationToken ct)
    {
        var classification = SqlStatementClassifier.Classify(sql);
        if (classification.Kind != SqlStatementKind.Write)
            throw new DatabaseOperationRefusedException($"Chỉ thực thi câu lệnh GHI đã được phê duyệt. {classification.Reason}");

        var table = SqlTargetTableParser.TryGetTargetTable(sql);
        if (table is null)
            throw new DatabaseOperationRefusedException("Không xác định được bảng mục tiêu để sao lưu an toàn — từ chối ghi.");

        var impl = _providers[provider];
        var egress = await VetAsync(impl, connectionString, ct);
        if (egress is not null) throw new DatabaseOperationRefusedException(egress);

        // The single-table backup only covers '{table}'. If the write can cascade to
        // other tables (FK CASCADE/SET NULL/SET DEFAULT) or fire triggers, that backup is
        // NOT a complete recovery point — refuse rather than promise false recoverability.
        var sideEffects = await impl.DetectWriteSideEffectsAsync(connectionString, table, _options.QueryTimeoutSeconds, ct);
        if (sideEffects.Count > 0)
            throw new DatabaseOperationRefusedException(
                $"Từ chối ghi vào '{table}': sao lưu chỉ bao phủ đúng bảng này, nhưng thao tác có thể thay đổi bảng KHÁC qua cascade/trigger nên bản sao lưu KHÔNG khôi phục đầy đủ được. " +
                $"Cần loại bỏ/tắt các phụ thuộc này hoặc sao lưu thủ công trước khi ghi. Chi tiết: {string.Join("; ", sideEffects)}.");

        // Back up BEFORE writing. A backup failure aborts the write (never write unbacked).
        var backup = await impl.BackupTableAsync(connectionString, table, _options.QueryTimeoutSeconds, ct);
        var affected = await impl.ExecuteWriteAsync(connectionString, sql, _options.QueryTimeoutSeconds, ct);
        return $"Đã sao lưu bảng '{table}' → '{backup}' (chỉ riêng bảng này), rồi thực thi. Số dòng ảnh hưởng: {affected}.";
    }

    private async Task<string?> VetAsync(IExternalDatabaseProvider impl, string connectionString, CancellationToken ct)
    {
        var host = impl.ExtractHost(connectionString);
        if (host is null) return null; // local/file engine — no network egress
        var result = await _egress.ValidateHostAsync(host, ct);
        return result.IsAllowed ? null : result.Reason;
    }
}
