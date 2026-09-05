using MySqlConnector;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// MySQL / MariaDB provider (via MySqlConnector). Read-only execution runs after
/// <c>SET SESSION TRANSACTION READ ONLY</c>, so the server rejects a data-modifying
/// statement even if the supplied account is over-privileged (the classifier upstream
/// is only defense-in-depth); the session flag is reset before the connection returns
/// to the pool. Rows and time are hard-capped.
/// </summary>
public sealed class MySqlDatabaseProvider : IExternalDatabaseProvider
{
    public DbProvider Provider => DbProvider.MySql;

    public string? ExtractHost(string connectionString)
    {
        try { return new MySqlConnectionStringBuilder(connectionString).Server; }
        catch { return null; }
    }

    public async Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Make the session read-only so the server refuses any write regardless of the
        // account's privileges — the real read-only gate for this path.
        await SetSessionAccessModeAsync(connection, readOnly: true, timeoutSeconds, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = timeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await PostgresDatabaseProvider.ReadCappedAsync(reader, maxRows, ct);
        }
        finally
        {
            // Reset before the connection is pooled so a later reuse isn't stuck read-only.
            try { await SetSessionAccessModeAsync(connection, readOnly: false, timeoutSeconds, ct); }
            catch { /* best effort; the pool also resets session state on reuse */ }
        }
    }

    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var columns = new Dictionary<(string, string), List<DbColumnInfo>>();
        var foreignKeys = new Dictionary<(string, string), List<string>>();

        await using (var fkCommand = connection.CreateCommand())
        {
            fkCommand.CommandTimeout = timeoutSeconds;
            fkCommand.CommandText = """
                SELECT table_schema, table_name, column_name, referenced_table_name, referenced_column_name
                FROM information_schema.key_column_usage
                WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL
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
                SELECT table_schema, table_name, column_name, data_type, is_nullable, column_key
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                ORDER BY table_schema, table_name, ordinal_position
                """;
            await using var reader = await colCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var key = (schema, table);
                if (!columns.TryGetValue(key, out var list)) columns[key] = list = new List<DbColumnInfo>();
                list.Add(new DbColumnInfo(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase),
                    reader.GetString(5).Equals("PRI", StringComparison.OrdinalIgnoreCase)));
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
        // {table} is a validated plain identifier from SqlTargetTableParser; the backup
        // name is derived from it → safe DDL.
        var backupName = $"lmkit_backup_{table.Replace('.', '_')}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE `{backupName}` AS SELECT * FROM {table}";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
        return backupName;
    }

    public async Task<int> ExecuteWriteAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DetectWriteSideEffectsAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct)
    {
        var (schema, name) = PostgresDatabaseProvider.SplitIdentifier(table);
        var risks = new List<string>();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Triggers on the target table (in this database, or the qualified schema).
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = """
                SELECT trigger_name
                FROM information_schema.triggers
                WHERE event_object_schema = COALESCE(@s, DATABASE()) AND event_object_table = @t
                """;
            command.Parameters.AddWithValue("@t", name);
            command.Parameters.AddWithValue("@s", (object?)schema ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"trigger '{reader.GetString(0)}' trên bảng '{name}'");
        }

        // Child tables whose FK references the target with a cascading action.
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = """
                SELECT rc.constraint_schema, rc.table_name, rc.update_rule, rc.delete_rule
                FROM information_schema.referential_constraints rc
                WHERE rc.referenced_table_name = @t
                  AND rc.constraint_schema = COALESCE(@s, DATABASE())
                  AND (rc.update_rule IN ('CASCADE','SET NULL','SET DEFAULT')
                       OR rc.delete_rule IN ('CASCADE','SET NULL','SET DEFAULT'))
                """;
            command.Parameters.AddWithValue("@t", name);
            command.Parameters.AddWithValue("@s", (object?)schema ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"khóa ngoại từ '{reader.GetString(0)}.{reader.GetString(1)}' → '{name}' (ON UPDATE {reader.GetString(2)}, ON DELETE {reader.GetString(3)})");
        }

        return risks;
    }

    private static async Task SetSessionAccessModeAsync(MySqlConnection connection, bool readOnly, int timeoutSeconds, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = readOnly ? "SET SESSION TRANSACTION READ ONLY" : "SET SESSION TRANSACTION READ WRITE";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
    }
}
