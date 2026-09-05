# LmKitOmniApi.IntegrationTests (opt-in, live-engine)

Live-engine integration tests for the external-database agent (GAP 2). They start **real**
database engines with [Testcontainers](https://dotnet.testcontainers.org/) and prove, per
engine, that:

- **(a)** a write (INSERT/UPDATE/DELETE) attempted on the **read path** does not persist —
  rejected at the **server** level by the read-only transaction (Postgres, MySQL), or rolled
  back (SQL Server has no read-only mode). The classifier is bypassed so this is a server-level proof.
- **(b)** `BackupTableAsync` makes a **real copy** of the target table before a write.
- **(c)** `IntrospectAsync` returns the **seeded tables**.

## Not part of the default suite

The fast default suite is run as:

```
dotnet test LmKitOmniApi.Tests/LmKitOmniApi.Tests.csproj
```

…which never references this project, so these tests can never slow it down or break it.

## Running the live tests

```
dotnet test LmKitOmniApi.IntegrationTests
```

**Docker is required.** Every test is a `[SkippableFact]`: if Docker is not running (or an
image can't be pulled), the container fails to start, the fixture records the reason, and the
test **skips** — it never fails. So the command above is safe to run in any environment; with
Docker it exercises the engines, without Docker it reports skips.

To run only these (they are also tagged `[Trait("Category","Integration")]`):

```
dotnet test LmKitOmniApi.IntegrationTests --filter "Category=Integration"
```

## Engines covered

| Engine     | Live container            | Notes |
|------------|---------------------------|-------|
| PostgreSQL | `postgres:16-alpine`      | server-level read-only rejection |
| MySQL      | `mysql:8.0`               | server-level read-only rejection |
| SQL Server | `mcr.microsoft.com/mssql/server` (Testcontainers default) | no read-only mode → transaction rollback |
| MongoDB    | `mongo:7`                 | no server read-only → classifier is the read gate; backup (`$out`) and schema sampling proven against the live container; the service's SSRF egress guard (which blocks the loopback container) is asserted live |
| **Oracle** | **none — inspection-only** | The official Oracle DB image is license/size-gated and unsuitable for routine CI. `OracleDatabaseProvider` (host extraction, read-only transaction, backup, cascade/trigger detection) is fully implemented and unit-covered, but has **no live container test**. |

### MongoDB note

`MongoDatabaseService` egress-vets every call, and a local container is only reachable on a
loopback/private address the SSRF guard always blocks. So the backup and schema **mechanics**
the service performs are exercised against the real container through the same MongoDB driver
the service uses, while the service's live egress refusal is asserted directly.
