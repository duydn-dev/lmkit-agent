using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Docker-free unit coverage for the relational providers (MySQL, SQL Server, Oracle).
/// The query/introspect/backup paths need a live engine and are exercised by opt-in
/// integration tests; what runs everywhere is the safety-critical, network-free part:
/// host extraction (the egress SSRF gate keys on it) and provider-registry wiring
/// (the enum + DI dictionary decide which engines the API will accept at all).
/// </summary>
public sealed class RelationalDatabaseProviderTests
{
    [Theory]
    [InlineData("Server=db.example.com;Port=3306;Database=app;User ID=ro;Password=x", "db.example.com")]
    [InlineData("Server=127.0.0.1;Database=app;User ID=ro;Password=x", "127.0.0.1")]
    public void MySql_ExtractHost_ReturnsServer(string connectionString, string expected)
    {
        Assert.Equal(expected, new MySqlDatabaseProvider().ExtractHost(connectionString));
    }

    [Theory]
    [InlineData("Server=tcp:sql.example.com,1433;Database=app;User ID=ro;Password=x;Encrypt=False", "sql.example.com")]
    [InlineData("Server=sql.example.com\\SQLEXPRESS;Database=app;User ID=ro;Password=x;Encrypt=False", "sql.example.com")]
    [InlineData("Server=10.0.0.5;Database=app;User ID=ro;Password=x;Encrypt=False", "10.0.0.5")]
    public void SqlServer_ExtractHost_NormalisesDataSource(string connectionString, string expected)
    {
        Assert.Equal(expected, new SqlServerDatabaseProvider().ExtractHost(connectionString));
    }

    [Theory]
    [InlineData("Data Source=oracle.example.com:1521/XEPDB1;User ID=ro;Password=x", "oracle.example.com")]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=tns.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=orcl)));User ID=ro;Password=x", "tns.example.com")]
    public void Oracle_ExtractHost_HandlesEzConnectAndTns(string connectionString, string expected)
    {
        Assert.Equal(expected, new OracleDatabaseProvider().ExtractHost(connectionString));
    }

    [Theory]
    [InlineData("MySql")]
    [InlineData("mysql")]
    [InlineData("SqlServer")]
    [InlineData("sqlserver")]
    [InlineData("Oracle")]
    public void TryParseProvider_AcceptsTheNewEngines_WhenRegistered(string name)
    {
        var service = new ExternalDatabaseService(
            new IExternalDatabaseProvider[]
            {
                new MySqlDatabaseProvider(),
                new SqlServerDatabaseProvider(),
                new OracleDatabaseProvider()
            },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions()));

        Assert.True(service.TryParseProvider(name, out _));
    }

    [Fact]
    public void TryParseProvider_RejectsAnUnregisteredEngine()
    {
        // Only MySQL registered → SQL Server parses to the enum but isn't available.
        var service = new ExternalDatabaseService(
            new IExternalDatabaseProvider[] { new MySqlDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions()));

        Assert.False(service.TryParseProvider("SqlServer", out _));
        Assert.False(service.TryParseProvider("Db2", out _)); // not even in the enum
    }

    [Fact]
    public void EachProvider_ReportsItsOwnEngine()
    {
        Assert.Equal(DbProvider.MySql, new MySqlDatabaseProvider().Provider);
        Assert.Equal(DbProvider.SqlServer, new SqlServerDatabaseProvider().Provider);
        Assert.Equal(DbProvider.Oracle, new OracleDatabaseProvider().Provider);
    }
}
