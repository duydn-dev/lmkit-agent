using Microsoft.Data.SqlClient;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Microsoft SQL Server provider. SQL Server has no server-side read-only transaction
/// mode, so the read path runs inside a transaction that is ALWAYS rolled back: any
/// state change that somehow slipped past the deterministic classifier (and the
/// least-privilege account, which remains the primary guarantee here) is undone rather
/// than committed. Rows and time are hard-capped.
/// </summary>
public sealed class SqlServerDatabaseProvider : IExternalDatabaseProvider
{
    public DbProvider Provider => DbProvider.SqlServer;

    public string? ExtractHost(string connectionString)
    {
        try
        {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource ?? string.Empty;
            // Normalise "tcp:host,1433" / "host\\INSTANCE" / "host,port" → bare host for egress vetting.
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) dataSource = dataSource[4..];
            var host = dataSource.Split(',', '\\')[0].Trim();
            return string.IsNullOrEmpty(host) ? null : host;
        }
        catch { return null; }
    }

    public async Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // No read-only txn in SQL Server: run inside a transaction and always roll back,
        // so nothing a statement changed is ever persisted.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = timeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await PostgresDatabaseProvider.ReadCappedAsync(reader, maxRows, ct);
        }
        finally
        {
            try { await transaction.RollbackAsync(ct); } catch { /* connection dropped mid-read */ }
        }
    }

    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var columns = new Dictionary<(string, string), List<DbColumnInfo>>();
        var primaryKeys = new HashSet<(string, string, string)>();
        var foreignKeys = new Dictionary<(string, string), List<string>>();

        await using (var pkCommand = connection.CreateCommand())
        {
            pkCommand.CommandTimeout = timeoutSeconds;
            pkCommand.CommandText = """
                SELECT tc.table_schema, tc.table_name, kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                """;
            await using var reader = await pkCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                primaryKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        await using (var fkCommand = connection.CreateCommand())
        {
            fkCommand.CommandTimeout = timeoutSeconds;
            fkCommand.CommandText = """
                SELECT sch.name, tp.name, cp.name, tr.name, cr.name
                FROM sys.foreign_key_columns fkc
                JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
                JOIN sys.schemas sch ON tp.schema_id = sch.schema_id
                JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
                JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
                JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
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
                WHERE table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')
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

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeoutSeconds;
        // CREATE SCHEMA must be alone in its batch → wrap it in EXEC.
        command.CommandText =
            "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'lmkit_backup') EXEC('CREATE SCHEMA lmkit_backup');" +
            $"SELECT * INTO lmkit_backup.[{backupName}] FROM {table};";
        await command.ExecuteNonQueryAsync(ct);
        return $"lmkit_backup.{backupName}";
    }

    public async Task<int> ExecuteWriteAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        return await command.ExecuteNonQueryAsync(ct);
    }
}
