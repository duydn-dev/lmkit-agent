using System.Text;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Turns an introspected table into one compact "card" (table + columns + keys)
/// that is embedded and retrieved as a unit. Kept small so a wide table still fits
/// a single embedding without scattering its columns across chunks — the schema
/// analogue of one RAG chunk per document.
/// </summary>
public static class SchemaCardBuilder
{
    public static string Build(DbTableInfo table)
    {
        var sb = new StringBuilder();
        var qualified = string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
        sb.Append("Table: ").AppendLine(qualified);

        if (table.Columns.Count > 0)
        {
            sb.AppendLine("Columns:");
            foreach (var column in table.Columns)
            {
                sb.Append("- ").Append(column.Name).Append(' ').Append(column.DataType);
                if (column.IsPrimaryKey) sb.Append(" PRIMARY KEY");
                if (!column.IsNullable) sb.Append(" NOT NULL");
                sb.AppendLine();
            }
        }

        if (table.ForeignKeys.Count > 0)
        {
            sb.AppendLine("Foreign keys:");
            foreach (var fk in table.ForeignKeys)
                sb.Append("- ").AppendLine(fk);
        }

        return sb.ToString().TrimEnd();
    }
}
