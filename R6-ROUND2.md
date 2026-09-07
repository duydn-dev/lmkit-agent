# R6 — Round 2: test hygiene / process-global state

Branch `round2/r6-test-hygiene`, based on master `79cb474`.
Baseline: **1100 passed / 0 failed / 9 skipped**. After this branch: **1101 / 0 / 9** (+1 new guard test).

Headline: there were **two** independent process-global bugs, not one. The cwd bug named in the
brief is real and I reproduced it at 9/10. While sweeping I found a second one — a shared,
recursively-deleted upload directory — and **that** is the one that actually reproduces as an
intermittent failure of the *full* suite (3 failures in 11 pre-fix runs, two different symptoms).

---

## (a) Process-global state found, and what I did about it

### 1. `Directory.SetCurrentDirectory` — FIXED

`LmKitOmniApi.Tests/LmModelRegistryTests.cs:17,20,25` (pre-fix line numbers)

```csharp
_previousDirectory = Directory.GetCurrentDirectory();
_modelsDirectory   = Path.Combine(Path.GetTempPath(), $"lmkit-registry-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(_modelsDirectory, "testmodel"));
Directory.SetCurrentDirectory(_modelsDirectory);   // process-global
```

The working directory is per-process. xUnit runs collections in parallel, and this fixture
re-entered the mutation once per test case (11 cases), so for 11 short windows every other test
in flight saw a temp folder as its working directory.

**Verified the mutation was never needed** (I did not take the previous analysis on trust):

| test | cwd-dependent? |
|---|---|
| `ParseRegisteredModels_ResolvesRelativePathAgainstModelsDirectory` | no — passes `_modelsDirectory` explicitly |
| `ParseRegisteredModels_WithMmprojMarksTwoFileVisionModel` | no — same |
| `ParseRegisteredModels_AcceptsAbsolutePaths` | no — same |
| `ParseRegisteredModels_RejectsParentTraversalOutsideModelsDirectory` (×3) | no — same |
| `ParseRegisteredModels_MissingPathThrows` | no — same |
| `Constructor_WithRegisteredModelConfiguration_DoesNotThrowAndKeepsDefaults` | no — `EndsWith` on the relative tail `AIModels/bonsai/model.gguf`, true for any cwd |
| `ResolveRegisteredModel_UnknownOrEmptyIdReturnsNull` | no — no path assertion |
| `ResolveModelsDirectory_DefaultsToAiModelsAndHonorsAbsoluteOverride` | no — `EndsWith("AIModels")` |

The `testmodel` subdirectory it created was referenced by nothing.

**Action:** removed `_previousDirectory` and both `SetCurrentDirectory` calls; kept the GUID temp
root (still deleted in `Dispose`); added a class-level comment naming the victim test so this is
not reintroduced.

**Stray-folder check (explicitly requested).** Three tests now resolve a *relative*
`AiModels:ModelsDirectory` against the test output directory instead of the temp folder. Nothing
is created: `LmModelManager.ResolveModelsDirectory` (`LmKitOmniApi/Services/LmModelManager.cs:176-182`)
only calls `Path.GetFullPath`, and the constructor's only disk touch is `File.Exists`. The
`Directory.CreateDirectory(cwd/"Models")` at `LmModelManager.cs:300-301` is on the HTTP *download*
path, which the constructor never reaches. `bin/Debug/net10.0/` contains no `AIModels` or `Models`
folder after 18 full runs. I added a regression test that pins this —
`LmModelRegistryTests.Constructor_WithRelativeModelsDirectory_CreatesNothingOnDisk` — using a GUID
folder name, so a future `CreateDirectory` in the constructor fails loudly instead of quietly
littering `bin/`. (This is the +1 test.)

### 2. Static cwd capture in a security boundary — CHANGED (see (e))

`LmKitOmniApi/Infrastructure/AI/Security/ToolSandboxService.cs:28-34` (pre-fix)

```csharp
private static readonly string[] DefaultAllowedPaths = new[]
{
    Path.Combine(Directory.GetCurrentDirectory(), "Uploads"), ... };
```

The sandbox roots were frozen inside the type initializer — i.e. at whatever instant some
unrelated code first touched the type. Every instance for the rest of the process inherited that
snapshot. Combined with (1) this is the mechanism of the reproduced flake.

**Action:** split policy from anchor. `AllowedRootFolderNames` (`{ "Uploads", "Documents",
"Temp", "wwwroot" }`) stays `static readonly` and immutable; the resolution against
`Directory.GetCurrentDirectory()` moved into `ResolveAllowedBasePaths()`, called from the
constructor. Same anchor, same folder set, same `Path.GetFullPath` normalisation — the boundary is
not widened by one byte, only the *timing* of the read changes.

### 3. Shared, recursively-deleted upload directory — FIXED (the real full-suite flake)

`LmKitOmniApi.Tests/PythonContainerExecutorTests.cs:18-19` and `162-169`

```csharp
private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
private static readonly Guid UserId   = Guid.Parse("22222222-2222-2222-2222-222222222222");
...
private static string UploadDir() =>
    Path.Combine(Directory.GetCurrentDirectory(), "Uploads", TenantId.ToString("N"), UserId.ToString("N"));
private static void CleanUploadDir() { ... Directory.Delete(dir, recursive: true); }
```

That GUID pair is **identical to `LmKitApiFactory.TenantId` / `LmKitApiFactory.UserId`**
(`LmKitOmniApi.Tests/ApiIntegrationTests.cs:260-261`), so `<cwd>/Uploads/11111111…/22222222…` was
shared with every `LmKitApiFactory`-based API test. `CleanUploadDir()` runs 10 times (before and
after ~5 tests, lines 536-659) and **recursively deletes that whole tree** — including the
`ChatAttachments/` subfolder — while `FilesControllerTests`, `ApiIntegrationTests` and
`VisionUploadApiTests` run in parallel collections and write files into it.

It fails in both directions, and I observed both:

* the API test loses its fixture →
  `FilesControllerTests.Download_ServesAnOwnedFile_WithItsContentType [FAIL]`
* the recursive delete hits a file the API host still has open →
  ```
  System.IO.IOException : The process cannot access the file '9195c3c194a149f68c6929cb250d975c.png'
    because it is being used by another process.
     at System.IO.FileSystem.RemoveDirectoryRecursive(...)
     at PythonContainerExecutorTests.CleanUploadDir() ... line 168
     at PythonContainerExecutorTests.ProducedFiles_AreCappedByMaxOutputFiles() ... line 572
  ```

**Action:** gave `PythonContainerExecutorTests` a unique identity
(`eeeeeeee-1111-…` / `ffffffff-2222-…`) so its recursive delete can only ever touch its own
subtree. The GUIDs are not asserted as literals anywhere; they are used only for `UploadDir()`
and as `ExecuteAsync` arguments. Comment added explaining why they must stay unique.

### 4. Victim hardening — `ToolSecurityPolicyTests` (no behaviour change)

`LmKitOmniApi.Tests/ToolSecurityPolicyTests.cs:29-37`. This test is the *victim* of (1)+(2), not a
cause. I left the assertion semantics alone (so a future reintroduction still fails, as it should)
but read the working directory once into a local and added a denial-reason to the failure message,
so the next occurrence is self-diagnosing instead of `Assert.True() Failure / Expected: True`.

### Checked and clear — no action needed

* **`Environment.SetEnvironmentVariable` / `Environment.CurrentDirectory`** — zero occurrences in
  the test project.
* **Culture** — no `CultureInfo.CurrentCulture` / `CurrentUICulture` / `DefaultThreadCurrentCulture`
  assignment anywhere.
* **`AppContext.SetSwitch`** — zero occurrences in tests or production code.
* **Fixed port bindings** — none. `QdrantVectorServiceTests.LivePort = 6334` and
  `SearchProviderTests.SearxLivePort = 8888` are *outbound* connections in skippable live smoke
  tests; nothing in the suite binds a listener.
* **`private static HttpClient? _ownerClient`** in 10 API test classes (`AgentRunsApiTests`,
  `AuditApiTests`, `CanvasApiTests`, `CustomInstructionsApiTests`, `DatabaseConnectionsApiTests`,
  `EphemeralChatTests`, `FilesControllerTests`, `McpServerOAuthApiTests`, `ProjectApiTests`,
  `VisionUploadApiTests`) — these are *per-class* private statics, each guarded by that class's own
  `static readonly SemaphoreSlim ClientGate`, and are deliberate (caching the login to stay under
  the auth rate limiter). No cross-class sharing, no leak. Left alone.
* **Temp fixtures** — every other `Path.GetTempPath()` fixture in the suite uses a `Guid.NewGuid()`
  leaf. `ComputerUseExecutorTests.cs:97` has a fixed `cu-test` *parent* but a GUID leaf, which is
  parallel-safe.
* **Other upload identities are distinct** — `ComputerUseExecutorTests` `aaaaaaaa…/bbbbbbbb…`,
  `ComputerUseAgentTests` `cccccccc…/dddddddd…`, `DocumentsApiFactoryBase` `77777777…/88888888…`.
  `DocumentsControllerTests` deletes single files, never a directory tree.
* **Production static caches exercised by tests** are all keyed, and the tests use unique keys:
  `QdrantClientFactory.SharedClients` (keyed by endpoint; tests use ports 6397/6398/6399),
  `McpOAuthTokenProvider.Cache` (keyed by `server.Id`, a fresh `Guid.NewGuid()` per test),
  `McpClientService.CachedTools` (keyed by tenant id), `MongoDatabaseService.Clients`,
  `AgentResiliencePolicy.LocalStates` (not touched by any test).
* **`[Collection]` / `DisableTestParallelization`** — the only collection definition is
  `DbSqliteCollection` (`DisableParallelization = true`, 11 SQLite classes). It was neither masking
  nor failing to mask these bugs: neither offender (`LmModelRegistryTests`,
  `PythonContainerExecutorTests`) is in it, and neither victim is either.
* **`xunit.runner.json`** — does not exist, and I deliberately did not add one. Turning parallelism
  down would have *hidden* both bugs rather than fixed them; the defaults are what surfaced them.

---

## (b) Instances in files owned by other agents — for routing

One item, in a coordinator-owned file:

**`LmKitOmniApi.Tests/ApiIntegrationTests.cs:133-165` and `167-197`** —
`ChatAttachment_IsDeletedAfterRequestProcessing` and
`ChatAttachments_RejectsTooManyFilesBeforeWritingScratchData` both snapshot
`<cwd>/Uploads/<LmKitApiFactory.TenantId>/<LmKitApiFactory.UserId>/ChatAttachments` with
`Directory.GetFiles` and assert exact set-equality (`filesBefore.SetEquals(filesAfter)`, lines 161
and 197). That is a process-global path keyed on an identity shared by four test classes, and an
*exact* set assertion over it is order-sensitive to anything else writing there. My fix removed
the one class that was recursively deleting it, so these are green now, but the assertion remains
brittle by construction — a future test that writes an upload for the factory identity will break
it. I did not touch the file (coordinator-owned). Suggested hardening: scope the snapshot to files
this test created (e.g. by name prefix) rather than the whole directory.

Nothing found in R2/R3/R4-owned files. I did not modify `StreamingGuardrailGateTests.cs`,
`ApprovalDetailsTests.cs`, `AgentRunApprovalLifecycleTests.cs`, `ApprovedActionScopeTests.cs`,
`ApiKeyAuthTests.cs`, `ApiIntegrationTests.cs`, or any `RateLimit*` / `ForwardedHeaders*` file.

---

## (c) Per-run pass/fail counts

`dotnet build` on `LmKitOmni.slnx`: **succeeded, 0 errors** (2 warnings, both pre-existing —
`LicenseManager.SetLicenseKey` obsolete).

### Targeted repro filter (4 classes: `LmModelRegistryTests`, `ToolSecurityPolicyTests`, `ComputerUseExecutorTests`, `PythonContainerExecutorTests`), default parallelism

| | pre-fix (58 tests) | post-fix (59 tests) |
|---|---|---|
| run 1 | 0 failed / 58 passed | 0 failed / 59 passed |
| run 2 | **1 failed** / 57 passed | 0 / 59 |
| run 3 | **1 failed** / 57 passed | 0 / 59 |
| run 4 | **1 failed** / 57 passed | 0 / 59 |
| run 5 | **1 failed** / 57 passed | 0 / 59 |
| run 6 | **1 failed** / 57 passed | 0 / 59 |
| run 7 | **1 failed** / 57 passed | 0 / 59 |
| run 8 | **1 failed** / 57 passed | 0 / 59 |
| run 9 | **1 failed** / 57 passed | 0 / 59 |
| run 10 | **1 failed** / 57 passed | 0 / 59 |
| runs 11-15 | — | 0 / 59 (×5) |
| **failure rate** | **9 / 10** | **0 / 15** |

### Full suite — pre-fix (master code, for comparison)

Default parallelism: 6 runs, all **1100 passed / 0 failed / 9 skipped**.

`xUnit.MaxParallelThreads=32`: 11 runs, **3 failures**:

| run | result | failing test |
|---|---|---|
| 7 | 1100 / 0 / 9 | — |
| 8 | 1100 / 0 / 9 | — |
| 9 | **1099 / 1 / 9** | `FilesControllerTests.Download_ServesAnOwnedFile_WithItsContentType` |
| 10 | 1100 / 0 / 9 | — |
| 11 | **1099 / 1 / 9** | `FilesControllerTests.Download_ServesAnOwnedFile_WithItsContentType` |
| 12-16 | 1100 / 0 / 9 (×5) | — |
| 17 | **1099 / 1 / 9** | `PythonContainerExecutorTests.ProducedFiles_AreCappedByMaxOutputFiles` (IOException in `CleanUploadDir`) |

### Full suite — post-fix (this branch)

| run | parallelism | result |
|---|---|---|
| 1 | default | 1101 passed / 0 failed / 9 skipped |
| 2 | default | 1101 / 0 / 9 |
| 3 | default | 1101 / 0 / 9 |
| 4 | default | 1101 / 0 / 9 |
| 5 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 6 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 7 | `MaxParallelThreads=1` (serialized) | 1101 / 0 / 9 |
| 8 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 9 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 10 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 11 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 12 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 13 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 14 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 15 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 16 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 17 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 18 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 19 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 20 | `MaxParallelThreads=32` | 1101 / 0 / 9 |
| 21 | `MaxParallelThreads=32` | 1101 / 0 / 9 |

**21 full runs, 0 failures**, including 14 at the same high-parallelism setting that produced 3
pre-fix failures. Plus 15 green runs of the targeted filter that failed 9/10 pre-fix.

Also run: the "uploads collision" subset (`PythonContainerExecutorTests` +
`FilesControllerTests` + `ApiIntegrationTests` + `DocumentsControllerTests` +
`VisionUploadApiTests`), 6 post-fix runs, all 57/0/0.

---

## (d) Did I reproduce the original flake? Yes — both of them.

**Honestly, with the caveats:**

* **The cwd flake: reproduced, decisively.** 9 failures in 10 runs of the 4-class filter,
  always the same test and line:
  `ToolSecurityPolicyTests.FileSandbox_DoesNotAcceptSiblingWithAllowedPrefix`,
  `ToolSecurityPolicyTests.cs:36`, `Assert.True() Failure / Expected: True / Actual: False`.
  Mechanism confirmed: the test builds `<cwd>/Uploads/payload.txt` at assert time while
  `LmModelRegistryTests` has cwd pointed at `%TEMP%/lmkit-registry-tests-<guid>`, and the
  `ToolSandboxService` roots were frozen at the real output directory — so the "allowed" path is
  outside the sandbox and is correctly denied. After the fix: 0 failures in 15 runs.
  **Caveat:** I could **not** get this one to fire in the *full* suite in 17 pre-fix runs. At 1100
  tests the two classes are a small fraction of the schedule and rarely overlap. So it is real and
  proven, but on this machine it is a rare full-suite event.

* **The uploads-directory flake: reproduced in the full suite**, 3 times in 11 pre-fix runs at
  `MaxParallelThreads=32` (0 in 6 runs at default parallelism — high concurrency is what makes it
  land). Two distinct symptoms, both traced to `PythonContainerExecutorTests.CleanUploadDir()`
  recursively deleting a directory shared with the `LmKitApiFactory` identity. Given the symptom
  profile — a full-suite failure that only shows up occasionally, in a file nobody had touched —
  **this is more likely than the cwd bug to be the intermittent failure that was actually being
  seen in CI.** I could not reproduce it with a 2-class filter (8 green runs); it needs the full
  suite's scheduling pressure.

* **What I did not do:** I have not proven the fix over hundreds of runs. 21 full runs including 14
  at the parallelism that reproduced 3 failures is strong evidence, not a proof. If either failure
  ever recurs, the diagnostic message I added to `ToolSecurityPolicyTests` will name the cause.

* **Noise disclaimer:** 5 other agents were building/testing concurrently in their own worktrees.
  I saw no failure attributable to that, and every failure I did observe was reproducible in a
  pattern tied to the code, not to load.

---

## (e) Verdict on `ToolSandboxService`: **changed**, narrowly, and here is why it is not a weakening

I considered defending it. The argument for leaving it alone is real: in production the working
directory is fixed by the host at startup and never moves, so the static capture is *correct* there,
and the test was undeniably the party at fault. That would have been a legitimate outcome.

I changed it anyway, for one reason: **the roots were determined by class-initialization order, not
by anything about the boundary itself.** A `static readonly` initialised from ambient mutable
process state means "whatever the working directory happened to be when some unrelated code first
touched this type". That is a bad property for a security check to have even when it happens to be
harmless, because its correctness rests on an invariant (cwd never changes) that was nowhere
written down and that the test suite violated.

What changed, precisely:

* the allow-list **contents** are unchanged: `Uploads`, `Documents`, `Temp`, `wwwroot`;
* the **anchor** is unchanged: `Directory.GetCurrentDirectory()`;
* the normalisation is unchanged: `Path.GetFullPath`, `StringComparer.OrdinalIgnoreCase`;
* `ValidateFilePath` — traversal rejection, blocked-pattern list, `Path.GetRelativePath` containment
  test — is untouched;
* only **when** the anchor is read moved: type-initialiser → constructor.

What I deliberately did **not** do:

* I did **not** make the roots configurable. There is no appsettings key, no constructor parameter,
  no caller-supplied path. Widening the sandbox still requires editing `AllowedRootFolderNames`.
* I did **not** switch the anchor to `AppContext.BaseDirectory`. It is more "stable", but under
  `dotnet run` it is `bin/Debug/netX` rather than the content root, so it would silently relocate
  the production `Uploads` root. Changing where a security boundary points is not a tidiness
  change.
* I did **not** inject `IHostEnvironment.ContentRootPath`. It would be equivalent in practice, but
  it adds a DI dependency to a class that is constructed directly in 12 test sites and buys nothing
  the constructor-time read does not already give.

The service is registered `AddScoped` (`Program.cs:246`), so the cost is four `Path.Combine` +
`Path.GetFullPath` calls per request — negligible. Note this also makes the sandbox *consistent*
with `UserResourceAccessService.GetUploadDirectory`
(`LmKitOmniApi/Infrastructure/AI/Security/UserResourceAccessService.cs:10-14`), which already read
the working directory at call time; before this change those two could in principle disagree.
