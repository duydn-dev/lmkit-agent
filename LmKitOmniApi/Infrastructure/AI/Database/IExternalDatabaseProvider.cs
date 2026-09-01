namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>Supported external database engines. Postgres is the production target; SQLite enables CI-testable, file-based connections.</summary>
public enum DbProvider
{
    Postgres,
    Sqlite
}

/// <summary>One introspected column (Phase 1 schema indexing).</summary>
public sealed record DbColumnInfo(string Name, string DataType, bool IsNullable, bool IsPrimaryKey);

/// <summary>One introspected table with its columns and foreign keys (Phase 1).</summary>
public sealed record DbTableInfo(string Schema, string Name, IReadOnlyList<DbColumnInfo> Columns, IReadOnlyList<string> ForeignKeys);

/// <summary>Tabular result of a read-only query, already stringified and row-capped.</summary>
public sealed record DbQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

/// <summary>
/// Per-engine connectivity + read-only execution. Implementations MUST enforce
/// read-only at the connection/transaction level (not by trusting the caller): the
/// statement classifier upstream is defense-in-depth, this is the real gate a
/// generated statement runs behind. Egress (host vetting) is applied by the calling
/// service before an implementation ever opens a socket.
/// </summary>
public interface IExternalDatabaseProvider
{
    DbProvider Provider { get; }

    /// <summary>The network host to vet for SSRF, or null for a local/file engine (SQLite).</summary>
    string? ExtractHost(string connectionString);

    /// <summary>Opens a connection and runs a trivial probe; throws on failure.</summary>
    Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct);

    /// <summary>
    /// Executes an already-classified read-only statement under a read-only
    /// transaction / read-only connection, capping rows and enforcing a timeout.
    /// </summary>
    Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct);

    /// <summary>Lists tables + columns + FKs for schema indexing (Phase 1).</summary>
    Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(string connectionString, int timeoutSeconds, CancellationToken ct);

    /// <summary>
    /// Snapshots <paramref name="table"/> (a validated plain identifier) into a
    /// timestamped backup copy BEFORE an approved write, returning the backup's
    /// name. Throws if the backup cannot be made — the caller must then refuse the
    /// write (never write without a backup).
    /// </summary>
    Task<string> BackupTableAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct);

    /// <summary>Executes an approved write statement (NOT read-only) and returns affected rows.</summary>
    Task<int> ExecuteWriteAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct);
}
