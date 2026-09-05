using System.Data;
using Npgsql;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// PostgreSQL provider. Read-only execution runs inside an explicit
/// <c>READ ONLY</c> transaction, so a data-modifying statement is rejected by the
/// server even if the supplied account is over-privileged (the classifier upstream
/// is only defense-in-depth). Rows and time are hard-capped.
/// </summary>
public sealed class PostgresDatabaseProvider : IExternalDatabaseProvider
{
    public DbProvider Provider => DbProvider.Postgres;

    public string? ExtractHost(string connectionString)
    {
        try { return new NpgsqlConnectionStringBuilder(connectionString).Host; }
        catch { return null; }
    }

    public async Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // READ ONLY transaction: the server refuses any write here regardless of the
        // account's privileges — the actual read-only guarantee for this path.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct);
        }

        // Read fully, then CLOSE the command+reader BEFORE rolling back: Npgsql forbids a
        // transaction op while a reader is still open on the connector ("a command is
        // already in progress"), so the reader must be disposed first.
        DbQueryResult result;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = timeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct);
            result = await ReadCappedAsync(reader, maxRows, ct);
        }
        await transaction.RollbackAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var columns = new Dictionary<(string, string), List<DbColumnInfo>>();
        var primaryKeys = new HashSet<(string, string, string)>();
        var foreignKeys = new Dictionary<(string, string), List<string>>();

        // Primary keys first, so column rows can be flagged.
        await using (var pkCommand = connection.CreateCommand())
        {
            pkCommand.CommandTimeout = timeoutSeconds;
            pkCommand.CommandText = """
                SELECT tc.table_schema, tc.table_name, kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
                """;
            await using var reader = await pkCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                primaryKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        await using (var fkCommand = connection.CreateCommand())
        {
            fkCommand.CommandTimeout = timeoutSeconds;
            fkCommand.CommandText = """
                SELECT tc.table_schema, tc.table_name, kcu.column_name,
                       ccu.table_name AS ref_table, ccu.column_name AS ref_column
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                JOIN information_schema.constraint_column_usage ccu
                  ON tc.constraint_name = ccu.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
                """;
            await using var reader = await fkCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!foreignKeys.TryGetValue(key, out var list)) foreignKeys[key] = list = new List<string>();
                list.Add($"{reader.GetString(2)} → {reader.GetString(3)}.{reader.GetString(4)}");
            }
        }

        await using (var colCommand = connection.CreateCommand())
        {
            colCommand.CommandTimeout = timeoutSeconds;
            colCommand.CommandText = """
                SELECT table_schema, table_name, column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY table_schema, table_name, ordinal_position
                """;
            await using var reader = await colCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var column = reader.GetString(2);
                var key = (schema, table);
                if (!columns.TryGetValue(key, out var list)) columns[key] = list = new List<DbColumnInfo>();
                list.Add(new DbColumnInfo(
                    column,
                    reader.GetString(3),
                    reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase),
                    primaryKeys.Contains((schema, table, column))));
            }
        }

        return columns.Select(entry => new DbTableInfo(
            entry.Key.Item1,
            entry.Key.Item2,
            entry.Value,
            foreignKeys.TryGetValue(entry.Key, out var fks) ? fks : new List<string>())).ToList();
    }

    public async Task<string> BackupTableAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct)
    {
        // {table} is a validated plain (optionally schema-qualified) identifier from
        // SqlTargetTableParser; the backup name is derived from it → safe DDL.
        var backupName = $"{table.Replace('.', '_')}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "CREATE SCHEMA IF NOT EXISTS lmkit_backup";
            schema.CommandTimeout = timeoutSeconds;
            await schema.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE lmkit_backup.\"{backupName}\" AS TABLE {table}";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
        return $"lmkit_backup.{backupName}";
    }

    public async Task<int> ExecuteWriteAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct)
    {
        // The approved write path — a normal (not read-only) connection.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DetectWriteSideEffectsAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct)
    {
        var (schema, name) = SplitIdentifier(table);
        var risks = new List<string>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // User triggers on the target table (tgisinternal filters FK/constraint plumbing).
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = """
                SELECT tg.tgname
                FROM pg_trigger tg
                JOIN pg_class c ON tg.tgrelid = c.oid
                JOIN pg_namespace n ON c.relnamespace = n.oid
                WHERE NOT tg.tgisinternal
                  AND c.relname = @t
                  AND (@s IS NULL OR n.nspname = @s)
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                """;
            command.Parameters.AddWithValue("t", name);
            command.Parameters.AddWithValue("s", (object?)schema ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"trigger '{reader.GetString(0)}' trên bảng '{name}'");
        }

        // Child tables whose FK references the target with a cascading action.
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = """
                SELECT tc.table_schema, tc.table_name, rc.update_rule, rc.delete_rule
                FROM information_schema.referential_constraints rc
                JOIN information_schema.table_constraints tc
                  ON tc.constraint_name = rc.constraint_name AND tc.constraint_schema = rc.constraint_schema
                JOIN information_schema.constraint_column_usage ccu
                  ON ccu.constraint_name = rc.unique_constraint_name AND ccu.constraint_schema = rc.unique_constraint_schema
                WHERE ccu.table_name = @t
                  AND (@s IS NULL OR ccu.table_schema = @s)
                  AND (rc.update_rule IN ('CASCADE','SET NULL','SET DEFAULT')
                       OR rc.delete_rule IN ('CASCADE','SET NULL','SET DEFAULT'))
                """;
            command.Parameters.AddWithValue("t", name);
            command.Parameters.AddWithValue("s", (object?)schema ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"khóa ngoại từ '{reader.GetString(0)}.{reader.GetString(1)}' → '{name}' (ON UPDATE {reader.GetString(2)}, ON DELETE {reader.GetString(3)})");
        }

        return risks;
    }

    // Splits an optionally schema-qualified identifier into (schema?, name).
    internal static (string? Schema, string Name) SplitIdentifier(string table)
    {
        var dot = table.LastIndexOf('.');
        return dot < 0 ? (null, table) : (table[..dot], table[(dot + 1)..]);
    }

    internal static async Task<DbQueryResult> ReadCappedAsync(System.Data.Common.DbDataReader reader, int maxRows, CancellationToken ct)
    {
        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i)?.ToString();
            rows.Add(row);
        }
        return new DbQueryResult(columns, rows, truncated);
    }
}
