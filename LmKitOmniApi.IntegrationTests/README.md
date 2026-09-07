# LmKitOmniApi.IntegrationTests (live-engine)

Live-engine integration tests. They start **real** engines with
[Testcontainers](https://dotnet.testcontainers.org/) and cover two things nothing else in the
repository can.

**External-database agent (GAP 2)** — per engine:

- **(a)** a write (INSERT/UPDATE/DELETE) attempted on the **read path** does not persist —
  rejected at the **server** level by the read-only transaction (Postgres, MySQL), or rolled
  back (SQL Server has no read-only mode). The classifier is bypassed so this is a server-level proof.
- **(b)** `BackupTableAsync` makes a **real copy** of the target table before a write.
- **(c)** `IntrospectAsync` returns the **seeded tables**.

**Migrations** (`PostgresMigrationTests`) — every migration is applied to a fresh, empty
PostgreSQL, and the resulting schema is compared against the EF model. The rest of the suite
builds its schema with `EnsureCreated()` from the model and never opens the migrations folder,
so a broken migration passes 1500+ tests; this is the only place that runs them. Its cheap
counterpart, `LmKitOmniApi.Tests/MigrationIntegrityTests`, diffs the model against the snapshot
and translates every migration through the Npgsql SQL generator with no database at all, in
about a second. What it cannot do is find out whether the server *accepts* the result — which
is what lives here.

## Not part of the fast suite

The fast suite is run as:

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
test **skips**. So the command above is safe to run on a laptop with no Docker daemon.

### That skip is opt-in

A skip that fires unconditionally is indistinguishable from a pass, and this project used to
swallow *every* startup failure that way — a bad image tag, an engine that refused the seed, a
bug in the fixture itself all reported green. Set

```
LMKIT_REQUIRE_CONTAINERS=1
```

and a container that will not start **fails** instead, with the original exception attached.
CI sets it (job `integration` in `.github/workflows/ci.yml`), so CI cannot pass without
actually starting every engine. `ContainerRequirementTests` covers both branches and needs no
Docker to do it.

To run only these (they are also tagged `[Trait("Category","Integration")]`):

```
dotnet test LmKitOmniApi.IntegrationTests --filter "Category=Integration"
```

## Engines covered

| Engine     | Live container            | Notes |
|------------|---------------------------|-------|
| PostgreSQL | `postgres:16-alpine`      | server-level read-only rejection; also the venue for the migration tests (its own container, one fresh database per test) |
| MySQL      | `mysql:8.0`               | server-level read-only rejection |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04` | no read-only mode → transaction rollback. Pinned rather than left to the module default, which changes under you on a package bump |
| MongoDB    | `mongo:7`                 | no server read-only → classifier is the read gate; backup (`$out`) and schema sampling proven against the live container; the service's SSRF egress guard (which blocks the loopback container) is asserted live |
| **Oracle** | **none — inspection-only** | The official Oracle DB image is license/size-gated and unsuitable for routine CI. `OracleDatabaseProvider` (host extraction, read-only transaction, backup, cascade/trigger detection) is fully implemented and unit-covered, but has **no live container test**. |

### MongoDB note

`MongoDatabaseService` egress-vets every call, and a local container is only reachable on a
loopback/private address the SSRF guard always blocks. So the backup and schema **mechanics**
the service performs are exercised against the real container through the same MongoDB driver
the service uses, while the service's live egress refusal is asserted directly.
