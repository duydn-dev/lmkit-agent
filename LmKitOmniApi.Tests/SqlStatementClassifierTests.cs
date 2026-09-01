using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The SQL read/write/refuse gate is defense-in-depth for the DB agent, so its
/// behavior — especially rejecting write/DDL/injection tricks — is pinned here.
/// </summary>
public class SqlStatementClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("select id, name from users where id = 1")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent")]
    [InlineData("EXPLAIN SELECT * FROM users")]
    [InlineData("SHOW TABLES")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("TABLE users")]
    [InlineData("SELECT /* a comment */ 1")]
    [InlineData("SELECT 1 -- ; DROP TABLE users")]          // the DROP is commented out → single read
    [InlineData("SELECT ';' AS semi")]                       // ';' lives in a string literal
    [InlineData("SELECT * FROM audit_log")]                  // 'log' substring must not trip LOAD/etc.
    public void Classifies_ReadOnly(string sql) =>
        Assert.Equal(SqlStatementKind.ReadOnly, SqlStatementClassifier.Classify(sql).Kind);

    [Theory]
    [InlineData("INSERT INTO users (name) VALUES ('x')")]
    [InlineData("UPDATE users SET name = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM users WHERE id = 1")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a")]
    [InlineData("REPLACE INTO users (id, name) VALUES (1, 'x')")]
    [InlineData("WITH d AS (DELETE FROM orders RETURNING *) SELECT * FROM d")] // CTE-wrapped write
    [InlineData("SELECT * FROM users FOR UPDATE")]           // row-lock = write intent
    public void Classifies_Write(string sql) =>
        Assert.Equal(SqlStatementKind.Write, SqlStatementClassifier.Classify(sql).Kind);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DROP TABLE users")]
    [InlineData("TRUNCATE TABLE users")]
    [InlineData("ALTER TABLE users ADD COLUMN x int")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("GRANT ALL ON users TO public")]
    [InlineData("SELECT 1; DROP TABLE users")]               // multiple statements
    [InlineData("SELECT * INTO backup FROM users")]          // SELECT … INTO creates a table
    [InlineData("SELECT pg_read_file('/etc/passwd')")]       // OS-reaching function
    [InlineData("SELECT * FROM users INTO OUTFILE '/tmp/x'")]// MySQL file write
    [InlineData("COPY users TO PROGRAM 'curl evil'")]        // Postgres COPY … TO PROGRAM
    [InlineData("SELECT load_file('/etc/passwd')")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$")]
    [InlineData("SET default_transaction_read_only = off")]
    [InlineData("VACUUM")]
    public void Classifies_Refused(string sql) =>
        Assert.Equal(SqlStatementKind.Refused, SqlStatementClassifier.Classify(sql).Kind);

    [Fact]
    public void TrailingSemicolon_OnASingleStatement_IsAllowed()
    {
        Assert.Equal(SqlStatementKind.ReadOnly, SqlStatementClassifier.Classify("SELECT 1;").Kind);
        Assert.Equal(SqlStatementKind.ReadOnly, SqlStatementClassifier.Classify("SELECT 1 ;  ").Kind);
    }
}
