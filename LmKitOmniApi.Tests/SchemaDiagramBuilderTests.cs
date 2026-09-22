using LmKitOmniApi.Infrastructure.AI.Database;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure tests for the schema-diagram graph: the FK strings every provider emits
/// ("customer_id → customers.id") must survive the trip into drawable edges, and a
/// relation the diagram cannot express must be reported rather than dropped.
/// </summary>
public sealed class SchemaDiagramBuilderTests
{
    private static DbTableInfo Table(string schema, string name, string[] foreignKeys, params DbColumnInfo[] columns) =>
        new(schema, name, columns, foreignKeys);

    [Fact]
    public void Build_FlagsKeyColumns_AndTurnsForeignKeysIntoEdges()
    {
        var tables = new[]
        {
            Table("main", "customers", [], new DbColumnInfo("id", "INTEGER", false, true), new DbColumnInfo("name", "TEXT", false, false)),
            Table("main", "orders", ["customer_id → customers.id"], new DbColumnInfo("id", "INTEGER", false, true), new DbColumnInfo("customer_id", "INTEGER", false, false))
        };

        var diagram = SchemaDiagramBuilder.Build(tables);

        Assert.False(diagram.Truncated);
        Assert.Equal(2, diagram.TotalTableCount);
        Assert.Equal(new[] { "customers", "orders" }, diagram.Tables.Select(t => t.QualifiedName));

        var orders = diagram.Tables.Single(t => t.Name == "orders");
        Assert.True(orders.Columns.Single(c => c.Name == "id").IsPrimaryKey);
        Assert.True(orders.Columns.Single(c => c.Name == "customer_id").IsForeignKey);
        Assert.False(orders.Columns.Single(c => c.Name == "id").IsForeignKey);

        var relation = Assert.Single(diagram.Relations);
        Assert.Equal("orders", relation.FromTable);
        Assert.Equal("customer_id", relation.FromColumn);
        Assert.Equal("customers", relation.ToTable);
        Assert.Equal("id", relation.ToColumn);
        Assert.True(relation.TargetIncluded);
    }

    [Fact]
    public void Build_MarksAnEdgeToATableOutsideTheDiagram()
    {
        // The referenced table was cut off by the cap (or lives in another schema): the edge
        // must say so instead of pointing at a node the client does not have.
        var tables = new[]
        {
            Table("main", "orders", ["customer_id → customers.id"], new DbColumnInfo("customer_id", "INTEGER", false, false))
        };

        var relation = Assert.Single(SchemaDiagramBuilder.Build(tables).Relations);

        Assert.False(relation.TargetIncluded);
        Assert.Equal("customers", relation.ToTable);
    }

    [Theory]
    [InlineData("customer_id", "", "", false)]                       // no arrow at all
    [InlineData("customer_id → customers", "customer_id", "customers", false)] // table known, column not
    [InlineData("a,b → t.x,y", "a,b", "", false)]                     // composite FK cannot be one edge
    [InlineData("", "", "", false)]
    public void TryParseForeignKey_KeepsUnparsableTextRawWithoutInventingAnEdge(
        string raw, string column, string table, bool resolved)
    {
        var parsed = SchemaDiagramBuilder.TryParseForeignKey(raw);

        Assert.Equal(column, parsed.Column);
        Assert.Equal(table, parsed.ReferencedTable);
        Assert.Empty(parsed.ReferencedColumn);
        Assert.Equal(resolved, parsed.IsResolved);
        Assert.Equal(raw, parsed.Raw);
    }

    [Fact]
    public void TryParseForeignKey_SplitsOnTheLastDot_ForASchemaQualifiedTarget()
    {
        var parsed = SchemaDiagramBuilder.TryParseForeignKey("user_id → public.users.id");

        Assert.True(parsed.IsResolved);
        Assert.Equal("user_id", parsed.Column);
        Assert.Equal("public.users", parsed.ReferencedTable);
        Assert.Equal("id", parsed.ReferencedColumn);
    }

    [Fact]
    public void TryParseForeignKey_AcceptsAnAsciiArrow()
    {
        var parsed = SchemaDiagramBuilder.TryParseForeignKey("user_id -> users.id");

        Assert.True(parsed.IsResolved);
        Assert.Equal("users", parsed.ReferencedTable);
    }

    [Fact]
    public void Build_ReportsTruncationInsteadOfSilentlyDroppingTables()
    {
        var tables = Enumerable.Range(1, 5)
            .Select(i => Table("main", $"t{i}", [], new DbColumnInfo("id", "INTEGER", false, true)))
            .ToList();

        var diagram = SchemaDiagramBuilder.Build(tables, maxTables: 2);

        Assert.True(diagram.Truncated);
        Assert.Equal(5, diagram.TotalTableCount);
        Assert.Equal(2, diagram.Tables.Count);
    }

    [Fact]
    public void Build_WithNoTables_IsAnEmptyDiagram()
    {
        var diagram = SchemaDiagramBuilder.Build([]);

        Assert.Empty(diagram.Tables);
        Assert.Empty(diagram.Relations);
        Assert.False(diagram.Truncated);
    }

    [Theory]
    [InlineData("main", "customers", "customers")]          // SQLite's implicit schema is noise
    [InlineData("", "customers", "customers")]
    [InlineData("public", "customers", "public.customers")]
    public void Qualify_DropsTheEnginePlaceholderSchema(string schema, string name, string expected)
    {
        Assert.Equal(expected, SchemaDiagramBuilder.Qualify(Table(schema, name, [])));
    }
}
