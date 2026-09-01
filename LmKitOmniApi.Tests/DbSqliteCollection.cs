namespace LmKitOmniApi.Tests;

/// <summary>
/// Serializes the test classes that open real SQLite files/connections. Under
/// xUnit's default class-level parallelism, several of these opening SQLite
/// simultaneously could race (native provider first-use + temp-file churn),
/// producing rare, non-deterministic failures. Sharing one non-parallel collection
/// makes them run one at a time — deterministic, at a negligible time cost.
/// </summary>
[CollectionDefinition("DbSqlite", DisableParallelization = true)]
public sealed class DbSqliteCollection { }
