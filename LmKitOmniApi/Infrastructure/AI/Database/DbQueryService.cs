using System.Text;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Backs the agent's two database tools. Deliberately calls NO chat model — it runs
/// inside the ReAct pass where the chat model is already busy, so (like RAG/run_python)
/// it must not re-enter it. Instead the agent writes the SQL itself from the schema
/// this service returns:
///   get_database_schema(question) → relevant schema (embedding retrieval only)
///   run_database_query(sql)       → execute READ-ONLY (classifier + read-only txn);
///                                   writes are refused here (approval + backup is a
///                                   later phase), unknown/DDL are refused outright.
/// Both accept an optional "db=&lt;name&gt;;" prefix to pick among multiple connections.
/// </summary>
public sealed class DbQueryService
{
    private const int SchemaTopK = 8;

    private readonly HermesDbContext _dbContext;
    private readonly ISchemaRetriever _schema;
    private readonly ExternalDatabaseService _databases;
    private readonly DbConnectionSecretProtector _protector;
    private readonly DatabaseAgentOptions _options;
    private readonly ILogger<DbQueryService> _logger;

    public DbQueryService(
        HermesDbContext dbContext,
        ISchemaRetriever schema,
        ExternalDatabaseService databases,
        DbConnectionSecretProtector protector,
        IOptions<DatabaseAgentOptions> options,
        ILogger<DbQueryService> logger)
    {
        _dbContext = dbContext;
        _schema = schema;
        _databases = databases;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>Returns the relevant schema for a request, with guidance to write a read-only query.</summary>
    public async Task<string> GetSchemaAsync(Guid tenantId, string input, CancellationToken ct)
    {
        var (nameHint, question) = ParseInput(input);
        var resolution = await ResolveConnectionAsync(tenantId, nameHint, ct);
        if (resolution.Message is not null) return resolution.Message;
        var connection = resolution.Connection!;

        var context = await _schema.RetrieveContextAsync(tenantId, connection.Id, question, SchemaTopK, ct);
        if (string.IsNullOrWhiteSpace(context))
            return $"[CSDL] Chưa lấy được schema cho '{connection.Name}'. Hãy lập chỉ mục lại kết nối rồi thử lại.";

        return $"""
            Cơ sở dữ liệu: {connection.Name} ({connection.Provider}).
            Schema liên quan:
            {context}

            Hãy viết MỘT câu SQL CHỈ-ĐỌC (SELECT/WITH…SELECT) trả lời yêu cầu, rồi gọi run_database_query với câu SQL đó.
            Nếu cần GHI dữ liệu (INSERT/UPDATE/DELETE), phải được người dùng phê duyệt — không tự chạy.
            """;
    }

    /// <summary>Executes an agent-written statement read-only; refuses writes (approval later) and DDL/unknown.</summary>
    public async Task<string> RunQueryAsync(Guid tenantId, string input, CancellationToken ct)
    {
        var (nameHint, sql) = ParseInput(input);
        var resolution = await ResolveConnectionAsync(tenantId, nameHint, ct);
        if (resolution.Message is not null) return resolution.Message;
        var connection = resolution.Connection!;

        if (!_databases.TryParseProvider(connection.Provider, out var provider))
            return $"[CSDL] Loại cơ sở dữ liệu không được hỗ trợ: {connection.Provider}.";

        var classification = SqlStatementClassifier.Classify(sql);
        switch (classification.Kind)
        {
            case SqlStatementKind.Write:
                return $"[CSDL] Câu lệnh này GHI dữ liệu nên KHÔNG được tự chạy — cần người dùng phê duyệt (và sao lưu trước). {classification.Reason}\nSQL:\n{sql.Trim()}";
            case SqlStatementKind.Refused:
                return $"[CSDL] Câu lệnh bị từ chối: {classification.Reason}\nSQL:\n{sql.Trim()}";
        }

        try
        {
            var connectionString = _protector.Unprotect(connection.ConnectionStringProtected);
            var result = await _databases.QueryReadOnlyAsync(provider, connectionString, sql, ct);
            return FormatResult(connection.Name, sql.Trim(), result);
        }
        catch (DatabaseOperationRefusedException ex)
        {
            return $"[CSDL] {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read-only query failed for connection {ConnectionId}.", connection.Id);
            var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return $"[CSDL] Truy vấn thất bại: {message}";
        }
    }

    private sealed record ConnectionResolution(DatabaseConnection? Connection, string? Message);

    private async Task<ConnectionResolution> ResolveConnectionAsync(Guid tenantId, string? nameHint, CancellationToken ct)
    {
        var connections = await _dbContext.DatabaseConnections
            .Where(c => c.TenantId == tenantId && c.IsActive && c.IsIndexed)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (connections.Count == 0)
            return new ConnectionResolution(null, "[CSDL] Chưa có kết nối cơ sở dữ liệu nào được lập chỉ mục. Hãy nhờ quản trị viên thêm và lập chỉ mục kết nối.");

        if (!string.IsNullOrWhiteSpace(nameHint))
        {
            var match = connections.FirstOrDefault(c => c.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return new ConnectionResolution(null, $"[CSDL] Không tìm thấy kết nối '{nameHint}'. Các kết nối: {string.Join(", ", connections.Select(c => c.Name))}.");
            return new ConnectionResolution(match, null);
        }

        if (connections.Count == 1)
            return new ConnectionResolution(connections[0], null);

        return new ConnectionResolution(null,
            $"[CSDL] Có nhiều kết nối: {string.Join(", ", connections.Select(c => c.Name))}. Hãy nêu rõ bằng tiền tố \"db=<tên>;\" trước yêu cầu/câu lệnh.");
    }

    /// <summary>Parses an optional leading "db=&lt;name&gt;;" selector; returns (name, remaining payload).</summary>
    private static (string? nameHint, string payload) ParseInput(string input)
    {
        var text = (input ?? string.Empty).TrimStart();
        if (text.StartsWith("db=", StringComparison.OrdinalIgnoreCase))
        {
            var semicolon = text.IndexOf(';');
            if (semicolon > 3)
                return (text[3..semicolon].Trim(), text[(semicolon + 1)..].Trim());
        }
        return (null, text.Trim());
    }

    private string FormatResult(string connectionName, string sql, DbQueryResult result)
    {
        var sb = new StringBuilder();
        sb.Append("[CSDL: ").Append(connectionName).AppendLine("]");
        sb.Append("SQL: ").AppendLine(sql);
        sb.Append("Kết quả (").Append(result.Rows.Count).Append(" dòng");
        if (result.Truncated) sb.Append(", đã cắt bớt tại ").Append(_options.MaxRows);
        sb.AppendLine("):");
        sb.AppendLine(string.Join(" | ", result.Columns));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(" | ", row.Select(cell => cell ?? "NULL")));
        return sb.ToString().TrimEnd();
    }
}
