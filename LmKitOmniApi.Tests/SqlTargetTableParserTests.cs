using LmKitOmniApi.Infrastructure.AI.Database;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The write path backs up the target table before writing; if the table can't be
/// pinned, the write is refused. This locks in that mapping (and the safe null).
/// </summary>
public class SqlTargetTableParserTests
{
    [Theory]
    [InlineData("UPDATE customers SET name = 'x' WHERE id = 1", "customers")]
    [InlineData("update  ONLY orders set total = 0", "orders")]
    [InlineData("DELETE FROM customers WHERE id = 1", "customers")]
    [InlineData("INSERT INTO customers (name) VALUES ('x')", "customers")]
    [InlineData("insert into orders values (1)", "orders")]
    [InlineData("REPLACE INTO customers (id) VALUES (1)", "customers")]
    [InlineData("UPDATE public.customers SET x = 1", "public.customers")]
    public void ResolvesTargetTable(string sql, string expected) =>
        Assert.Equal(expected, SqlTargetTableParser.TryGetTargetTable(sql));

    [Theory]
    [InlineData("")]
    [InlineData("SELECT * FROM customers")]                     // not a write
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT 1")] // CTE — no leading write verb
    [InlineData("UPDATE \"weird table\" SET x = 1")]            // quoted/spaced — refuse (null → write refused)
    [InlineData("DROP TABLE customers")]                        // not a DML write
    public void ReturnsNull_WhenTargetCannotBePinned(string sql) =>
        Assert.Null(SqlTargetTableParser.TryGetTargetTable(sql));
}
