namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>One column in the diagram, flagged with its key role so a client can badge it.</summary>
public sealed record DbDiagramColumn(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsForeignKey);

/// <summary>
/// One foreign key. <see cref="IsResolved"/> is false when the introspection string
/// could not be split into (table, column) — the raw text is kept so the client can
/// still show it instead of silently dropping a relation.
/// </summary>
public sealed record DbDiagramForeignKey(
    string Column,
    string ReferencedTable,
    string ReferencedColumn,
    string Raw,
    bool IsResolved);

/// <summary>One table (entity) of the diagram.</summary>
public sealed record DbDiagramTable(
    string Schema,
    string Name,
    string QualifiedName,
    IReadOnlyList<DbDiagramColumn> Columns,
    IReadOnlyList<DbDiagramForeignKey> ForeignKeys);

/// <summary>
/// A drawable edge: <paramref name="FromTable"/>/<paramref name="FromColumn"/> (child, holds
/// the FK) points at <paramref name="ToTable"/>/<paramref name="ToColumn"/> (parent).
/// <see cref="TargetIncluded"/> is false when the parent table is outside the emitted set
/// (e.g. cut off by the table cap, or living in another schema) — a client must NOT draw an
/// edge to an entity it has no node for, but should still be able to list it.
/// </summary>
public sealed record DbDiagramRelation(
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    bool TargetIncluded);

/// <summary>The whole diagram: entities plus the relations between them.</summary>
public sealed record DbSchemaDiagram(
    IReadOnlyList<DbDiagramTable> Tables,
    IReadOnlyList<DbDiagramRelation> Relations,
    int TotalTableCount,
    bool Truncated);

/// <summary>
/// Turns introspected tables into a diagram graph. Pure and provider-agnostic: it works from
/// the same <see cref="DbTableInfo"/> the schema index is built from, so the picture a
/// reviewer sees is the same shape the agent's SQL generation works from.
///
/// Foreign keys arrive as the human-readable strings the providers emit
/// (<c>"customer_id → customers.id"</c> — identical format across Postgres/SQL Server/MySQL/
/// SQLite), so they are parsed here rather than in every client.
/// </summary>
public static class SchemaDiagramBuilder
{
    /// <summary>Rendering cap: beyond this the diagram is unreadable and one screenshot cannot help.</summary>
    public const int DefaultMaxTables = 40;

    public static DbSchemaDiagram Build(IReadOnlyList<DbTableInfo> tables, int maxTables = DefaultMaxTables)
    {
        if (maxTables < 1) maxTables = DefaultMaxTables;

        var total = tables.Count;
        var truncated = total > maxTables;
        var selected = truncated ? tables.Take(maxTables).ToList() : tables.ToList();

        // Only tables actually emitted can be an edge target.
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in selected)
        {
            included.Add(Qualify(table));
            included.Add(table.Name);
        }

        var diagramTables = new List<DbDiagramTable>(selected.Count);
        var relations = new List<DbDiagramRelation>();

        foreach (var table in selected)
        {
            var qualified = Qualify(table);
            var foreignKeys = new List<DbDiagramForeignKey>();
            var fkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in table.ForeignKeys)
            {
                var parsed = TryParseForeignKey(raw);
                foreignKeys.Add(parsed);

                if (string.IsNullOrWhiteSpace(parsed.Column)) continue;
                fkColumns.Add(parsed.Column);

                if (!parsed.IsResolved) continue;

                relations.Add(new DbDiagramRelation(
                    FromTable: qualified,
                    FromColumn: parsed.Column,
                    ToTable: parsed.ReferencedTable,
                    ToColumn: parsed.ReferencedColumn,
                    TargetIncluded: included.Contains(parsed.ReferencedTable)));
            }

            var columns = table.Columns
                .Select(c => new DbDiagramColumn(
                    c.Name,
                    c.DataType,
                    c.IsNullable,
                    c.IsPrimaryKey,
                    fkColumns.Contains(c.Name)))
                .ToList();

            diagramTables.Add(new DbDiagramTable(table.Schema, table.Name, qualified, columns, foreignKeys));
        }

        return new DbSchemaDiagram(diagramTables, relations, total, truncated);
    }

    /// <summary>"schema.table", or the bare name when the engine has no meaningful schema (SQLite's "main").</summary>
    public static string Qualify(DbTableInfo table) =>
        string.IsNullOrEmpty(table.Schema) || string.Equals(table.Schema, "main", StringComparison.OrdinalIgnoreCase)
            ? table.Name
            : $"{table.Schema}.{table.Name}";

    /// <summary>
    /// Parses the providers' FK text back into (column, referenced table, referenced column).
    /// The split is on the LAST dot because the referenced side may be schema-qualified
    /// ("public.users.id") while the referenced table is a bare name in the common case.
    /// </summary>
    public static DbDiagramForeignKey TryParseForeignKey(string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return new DbDiagramForeignKey(string.Empty, string.Empty, string.Empty, text, false);

        var separator = text.Contains('→') ? '→' : text.Contains("->", StringComparison.Ordinal) ? '-' : '\0';
        if (separator == '\0') return new DbDiagramForeignKey(string.Empty, string.Empty, string.Empty, text, false);

        var parts = separator == '-'
            ? text.Split("->", 2, StringSplitOptions.TrimEntries)
            : text.Split('→', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return new DbDiagramForeignKey(string.Empty, string.Empty, string.Empty, text, false);

        var column = parts[0];
        // A composite FK arrives as "a,b → t.x,y": the drawing cannot express it, keep it raw.
        if (column.Contains(',') || parts[1].Contains(','))
            return new DbDiagramForeignKey(column, string.Empty, string.Empty, text, false);

        var target = parts[1];
        var split = target.LastIndexOf('.');
        if (split <= 0 || split == target.Length - 1)
            return new DbDiagramForeignKey(column, target, string.Empty, text, false);

        return new DbDiagramForeignKey(
            column,
            target[..split],
            target[(split + 1)..],
            text,
            IsResolved: !string.IsNullOrWhiteSpace(column));
    }
}
