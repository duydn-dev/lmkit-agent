using System.Text;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>Read vs write vs refuse classification of a single SQL statement.</summary>
public enum SqlStatementKind
{
    /// <summary>Provably read-only: safe to run directly (still under a read-only transaction).</summary>
    ReadOnly,
    /// <summary>A recognized data-modifying statement: allowed ONLY via HITL approval + backup.</summary>
    Write,
    /// <summary>DDL / procedural / dangerous / multi-statement / unclassifiable: never executed.</summary>
    Refused
}

public sealed record SqlClassification(SqlStatementKind Kind, string Reason);

/// <summary>
/// Deterministic, conservative classifier for LLM-generated SQL. This is
/// DEFENSE-IN-DEPTH and telemetry, NOT the security boundary — the real boundary is
/// a least-privilege read-only DB account plus a read-only transaction. The design
/// errs toward <see cref="SqlStatementKind.Refused"/>: a statement is
/// <see cref="SqlStatementKind.ReadOnly"/> only if it is a SINGLE statement that
/// BEGINS with a read verb and contains no data-modifying token, no SELECT…INTO,
/// and no OS/file-reaching function. Recognized DML is surfaced as
/// <see cref="SqlStatementKind.Write"/> (approval + backup required). DDL,
/// procedural, transaction/session, multi-statement, or unknown → refused.
/// </summary>
public static class SqlStatementClassifier
{
    private static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // A read-only statement must BEGIN with one of these.
    private static readonly HashSet<string> ReadLeadingVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WITH", "EXPLAIN", "SHOW", "VALUES", "TABLE" };

    // Recognized data-modifying leading verbs → Write (approvable), not refused.
    private static readonly HashSet<string> WriteLeadingVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT" };

    // Data-modifying tokens that force a Write classification even under a read verb
    // (CTE-wrapped writes; SELECT … FOR UPDATE/SHARE row locks = write intent).
    private static readonly HashSet<string> WriteTokens = new(StringComparer.OrdinalIgnoreCase)
        { "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT" };

    // Dangerous as a LEADING verb (DDL / privilege / procedural / transaction /
    // session control). These are legitimate column names elsewhere, so they are
    // only refused when they START the statement — the read path already excludes them.
    private static readonly HashSet<string> RefusedLeadingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "DROP", "ALTER", "TRUNCATE", "CREATE", "GRANT", "REVOKE", "RENAME",
        "ATTACH", "DETACH", "VACUUM", "REINDEX", "ANALYZE", "CLUSTER",
        "CALL", "EXEC", "EXECUTE", "DO", "DECLARE", "PREPARE", "DEALLOCATE",
        "SET", "RESET", "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "LOCK", "UNLOCK", "USE", "COPY", "LOAD", "PRAGMA"
    };

    // OS/file-reaching functions and clauses that are never plausible bare column
    // identifiers — refused wherever they appear (outside stripped literals/comments).
    private static readonly HashSet<string> DangerousAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_sleep",
        "lo_import", "lo_export", "dblink", "xp_cmdshell", "sp_executesql",
        "openrowset", "openquery", "opendatasource", "load_file",
        "outfile", "dumpfile", "utl_file", "dbms_sql", "waitfor"
    };

    public static SqlClassification Classify(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new SqlClassification(SqlStatementKind.Refused, "Câu lệnh rỗng.");

        var cleaned = StripCommentsAndLiterals(sql).Trim();

        // Drop a single trailing terminator; anything left means multiple statements.
        if (cleaned.EndsWith(';')) cleaned = cleaned[..^1].TrimEnd();
        if (cleaned.Contains(';'))
            return new SqlClassification(SqlStatementKind.Refused, "Chỉ cho phép một câu lệnh.");
        if (cleaned.Length == 0)
            return new SqlClassification(SqlStatementKind.Refused, "Câu lệnh rỗng.");

        var tokens = Token.Matches(cleaned).Select(m => m.Value).ToList();
        if (tokens.Count == 0)
            return new SqlClassification(SqlStatementKind.Refused, "Không nhận diện được câu lệnh.");

        var tokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        var leading = tokens[0];

        // OS/file-reaching functions → refuse regardless of leading verb.
        var dangerous = DangerousAnywhere.FirstOrDefault(d => tokenSet.Contains(d));
        if (dangerous is not null)
            return new SqlClassification(SqlStatementKind.Refused, $"Hàm/mệnh đề nguy hiểm: {dangerous.ToUpperInvariant()}.");

        if (WriteLeadingVerbs.Contains(leading))
            return new SqlClassification(SqlStatementKind.Write, $"Câu lệnh ghi dữ liệu ({leading.ToUpperInvariant()}).");

        if (RefusedLeadingVerbs.Contains(leading))
            return new SqlClassification(SqlStatementKind.Refused, $"Câu lệnh không được phép ({leading.ToUpperInvariant()}).");

        if (ReadLeadingVerbs.Contains(leading))
        {
            var write = WriteTokens.FirstOrDefault(w => tokenSet.Contains(w));
            if (write is not null)
                return new SqlClassification(SqlStatementKind.Write, $"Ghi dữ liệu ẩn trong câu lệnh ({write.ToUpperInvariant()}).");

            if (tokenSet.Contains("INTO"))
                return new SqlClassification(SqlStatementKind.Refused, "SELECT … INTO tạo bảng — không cho phép.");

            return new SqlClassification(SqlStatementKind.ReadOnly, $"Truy vấn chỉ-đọc ({leading.ToUpperInvariant()}).");
        }

        return new SqlClassification(SqlStatementKind.Refused, $"Câu lệnh không được hỗ trợ ({leading.ToUpperInvariant()}).");
    }

    /// <summary>
    /// Removes SQL comments and single-quoted string literals so keyword/`;` scanning
    /// cannot be fooled by their content. Line comments <c>--</c> and <c>#</c> to end
    /// of line; block comments <c>/* … */</c>; string literals <c>'…'</c> with the
    /// doubled-quote escape. Double-quoted identifiers are left intact (they are names,
    /// not strings); a reserved word used as a quoted identifier classifies
    /// conservatively, which is the safe direction.
    /// </summary>
    private static string StripCommentsAndLiterals(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if ((c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') || c == '#')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                sb.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
                sb.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
