using Microsoft.Data.Sqlite;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// SQLite provider (file-based). Read-only execution forces
/// <see cref="SqliteOpenMode.ReadOnly"/> on the connection, so the driver opens the
/// file read-only and any write fails — the read-only guarantee for this engine.
/// Primarily used for CI-testable, file-based connections.
/// </summary>
public sealed class SqliteDatabaseProvider : IExternalDatabaseProvider
{
    public DbProvider Provider => DbProvider.Sqlite;

    // SQLite is a local file, not a network endpoint — no host to vet for SSRF.
    public string? ExtractHost(string connectionString) => null;

    public async Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ForceReadOnly(connectionString));
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await PostgresDatabaseProvider.ReadCappedAsync(reader, maxRows, ct);
    }

    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ForceReadOnly(connectionString));
        await connection.OpenAsync(ct);

        var tableNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            command.CommandTimeout = timeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tableNames.Add(reader.GetString(0));
        }

        var tables = new List<DbTableInfo>();
        foreach (var table in tableNames)
        {
            var quoted = table.Replace("'", "''");

            var columns = new List<DbColumnInfo>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info('{quoted}')";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // cid, name, type, notnull, dflt_value, pk
                    var name = reader.GetString(1);
                    var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var notNull = reader.GetInt32(3) == 1;
                    var isPk = reader.GetInt32(5) > 0;
                    columns.Add(new DbColumnInfo(name, type, !notNull, isPk));
                }
            }

            var foreignKeys = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list('{quoted}')";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // id, seq, table, from, to, ...
                    var refTable = reader.GetString(2);
                    var from = reader.GetString(3);
                    var to = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    foreignKeys.Add($"{from} → {refTable}.{to}");
                }
            }

            tables.Add(new DbTableInfo("main", table, columns, foreignKeys));
        }

        return tables;
    }

    private static string ForceReadOnly(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly };
        return builder.ToString();
    }
}
