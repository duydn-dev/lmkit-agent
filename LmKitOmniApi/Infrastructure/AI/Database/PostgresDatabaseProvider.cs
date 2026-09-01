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

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = await ReadCappedAsync(reader, maxRows, ct);
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
