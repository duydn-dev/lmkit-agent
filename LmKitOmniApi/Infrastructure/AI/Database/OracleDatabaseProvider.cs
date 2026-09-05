using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Oracle provider (via Oracle.ManagedDataAccess.Core). Read-only execution runs after
/// <c>SET TRANSACTION READ ONLY</c>, so the server raises ORA-01456 on any write in
/// that transaction regardless of the account's privileges (the classifier upstream is
/// only defense-in-depth). Introspection is scoped to the connecting schema's own
/// objects (USER_* catalog views). Rows and time are hard-capped.
/// </summary>
public sealed class OracleDatabaseProvider : IExternalDatabaseProvider
{
    public DbProvider Provider => DbProvider.Oracle;

    public string? ExtractHost(string connectionString)
    {
        try
        {
            var dataSource = new OracleConnectionStringBuilder(connectionString).DataSource ?? string.Empty;
            // TNS descriptor form: (DESCRIPTION=(ADDRESS=(PROTOCOL=tcp)(HOST=db.example.com)(PORT=1521))...)
            var tns = Regex.Match(dataSource, @"HOST\s*=\s*([^)\s]+)", RegexOptions.IgnoreCase);
            if (tns.Success) return tns.Groups[1].Value.Trim();
            // EZConnect form: [//]host[:port][/service]
            var ez = dataSource.TrimStart('/').Split(':', '/')[0].Trim();
            return string.IsNullOrEmpty(ez) ? null : ez;
        }
        catch { return null; }
    }

    public async Task TestConnectionAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DUAL";
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbQueryResult> ExecuteReadOnlyAsync(
        string connectionString, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);

        // Read-only transaction: Oracle rejects any DML here (ORA-01456).
        await ExecuteAsync(connection, "SET TRANSACTION READ ONLY", timeoutSeconds, ct);
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
            try { await ExecuteAsync(connection, "ROLLBACK", timeoutSeconds, ct); } catch { /* best effort */ }
        }
    }

    public async Task<IReadOnlyList<DbTableInfo>> IntrospectAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);

        var schema = "";
        await using (var userCommand = connection.CreateCommand())
        {
            userCommand.CommandText = "SELECT USER FROM DUAL";
            userCommand.CommandTimeout = timeoutSeconds;
            schema = (await userCommand.ExecuteScalarAsync(ct))?.ToString() ?? "";
        }

        var columns = new Dictionary<string, List<DbColumnInfo>>();
        var primaryKeys = new HashSet<(string, string)>();
        var foreignKeys = new Dictionary<string, List<string>>();

        await using (var pkCommand = connection.CreateCommand())
        {
            pkCommand.CommandTimeout = timeoutSeconds;
            pkCommand.CommandText = """
                SELECT ucc.table_name, ucc.column_name
                FROM user_constraints uc
                JOIN user_cons_columns ucc ON uc.constraint_name = ucc.constraint_name
                WHERE uc.constraint_type = 'P'
                """;
            await using var reader = await pkCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                primaryKeys.Add((reader.GetString(0), reader.GetString(1)));
        }

        await using (var fkCommand = connection.CreateCommand())
        {
            fkCommand.CommandTimeout = timeoutSeconds;
            fkCommand.CommandText = """
                SELECT ucc.table_name, ucc.column_name, r.table_name, r.column_name
                FROM user_constraints uc
                JOIN user_cons_columns ucc ON uc.constraint_name = ucc.constraint_name
                JOIN user_cons_columns r ON uc.r_constraint_name = r.constraint_name AND ucc.position = r.position
                WHERE uc.constraint_type = 'R'
                """;
            await using var reader = await fkCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!foreignKeys.TryGetValue(table, out var list)) foreignKeys[table] = list = new List<string>();
                list.Add($"{reader.GetString(1)} → {reader.GetString(2)}.{reader.GetString(3)}");
            }
        }

        await using (var colCommand = connection.CreateCommand())
        {
            colCommand.CommandTimeout = timeoutSeconds;
            colCommand.CommandText = """
                SELECT table_name, column_name, data_type, nullable
                FROM user_tab_columns
                WHERE table_name IN (SELECT table_name FROM user_tables)
                ORDER BY table_name, column_id
                """;
            await using var reader = await colCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                var column = reader.GetString(1);
                if (!columns.TryGetValue(table, out var list)) columns[table] = list = new List<DbColumnInfo>();
                list.Add(new DbColumnInfo(
                    column,
                    reader.GetString(2),
                    reader.GetString(3).Equals("Y", StringComparison.OrdinalIgnoreCase),
                    primaryKeys.Contains((table, column))));
            }
        }

        return columns.Select(entry => new DbTableInfo(
            schema,
            entry.Key,
            entry.Value,
            foreignKeys.TryGetValue(entry.Key, out var fks) ? fks : new List<string>())).ToList();
    }

    public async Task<string> BackupTableAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct)
    {
        // {table} is a validated plain identifier from SqlTargetTableParser; the backup
        // name is derived from it → safe DDL. Kept short for Oracle identifier limits.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var baseName = table.Replace('.', '_');
        if (baseName.Length > 100) baseName = baseName[..100];
        var backupName = $"BKP_{baseName}_{stamp}";

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        // CTAS is DDL and auto-commits in Oracle, so the snapshot persists.
        await ExecuteAsync(connection, $"CREATE TABLE \"{backupName}\" AS SELECT * FROM {table}", timeoutSeconds, ct);
        return backupName;
    }

    public async Task<int> ExecuteWriteAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        int affected;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.CommandTimeout = timeoutSeconds;
            affected = await command.ExecuteNonQueryAsync(ct);
        }
        // Oracle DML is not auto-committed — commit the approved write explicitly.
        await ExecuteAsync(connection, "COMMIT", timeoutSeconds, ct);
        return affected;
    }

    public async Task<IReadOnlyList<string>> DetectWriteSideEffectsAsync(string connectionString, string table, int timeoutSeconds, CancellationToken ct)
    {
        // Oracle stores unquoted identifiers upper-cased; compare on UPPER(:t). Oracle
        // supports only ON DELETE CASCADE / SET NULL (no ON UPDATE actions).
        var bareTable = table.Contains('.') ? table[(table.LastIndexOf('.') + 1)..] : table;
        var risks = new List<string>();

        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var command = (OracleCommand)connection.CreateCommand())
        {
            command.BindByName = true;
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = "SELECT trigger_name FROM user_triggers WHERE table_name = UPPER(:t)";
            command.Parameters.Add(new OracleParameter("t", bareTable));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"trigger '{reader.GetString(0)}' trên bảng '{bareTable}'");
        }

        await using (var command = (OracleCommand)connection.CreateCommand())
        {
            command.BindByName = true;
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = """
                SELECT uc.table_name, uc.delete_rule
                FROM user_constraints uc
                WHERE uc.constraint_type = 'R'
                  AND uc.delete_rule IN ('CASCADE', 'SET NULL')
                  AND uc.r_constraint_name IN (
                      SELECT constraint_name FROM user_constraints
                      WHERE table_name = UPPER(:t) AND constraint_type IN ('P', 'U'))
                """;
            command.Parameters.Add(new OracleParameter("t", bareTable));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                risks.Add($"khóa ngoại từ '{reader.GetString(0)}' → '{bareTable}' (ON DELETE {reader.GetString(1)})");
        }

        return risks;
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
    }
}
