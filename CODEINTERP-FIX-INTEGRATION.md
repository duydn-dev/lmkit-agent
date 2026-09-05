# Code Interpreter — Fix Integration Guide

Two audit-confirmed gaps in the code-interpreter stack are fixed here:

- **GAP 1 (P1) — Python container timeout kills the docker CLI, not the container.**
  On a wall-clock timeout / cancellation the `IProcessRunner` kills the `docker run`
  **CLI process tree**, which on daemon Docker does **not** stop the daemon-managed
  container — a signal-ignoring script could outlive the reported timeout. Fixed by
  launching with `--cidfile` and issuing a fallback `docker kill` / `docker rm -f`.
- **GAP 2 (P2) — JS/Jint was not gate-able like Python.** `run_javascript` was
  registered whenever `ActionAllowed("CODE")`, with no feature toggle (unlike Python).
  Fixed by adding a `JavaScriptEnabled` option (**default `true`**, so behavior is
  unchanged) surfaced as `IExecutionSandboxEngine.IsEnabled`.

---

## GAP 1 — needs NO manual integration ✅

The fix is **fully self-contained** in `Infrastructure/AI/Security/PythonContainerExecutor.cs`
and uses the **already-injected, mockable `IProcessRunner`**. Nothing in any DO-NOT-EDIT
file changes.

What it does:

- The hardened `docker run` now includes `--cidfile <tmp>` (kept as a **sibling** of the
  `/work` scratch dir, never inside it, so it is neither mounted into the container nor
  harvested as a produced file). `--rm` is retained.
- On **timeout** *and* on **caller cancellation** (and defensively on a launch-time
  fault), the executor reads the container id the CLI wrote to the cidfile and issues an
  explicit `docker kill <id>` through the **same** `IProcessRunner`; if the kill does not
  take (non-zero / timed out), it falls back to `docker rm -f <id>`. The teardown runs on
  `CancellationToken.None` with its own short budget so it still fires when the caller
  cancelled. It is best-effort: if no id was written (the container never started) it is a
  no-op, and a **normal (non-timeout) run issues no kill**.
- The cidfile is deleted in `finally` alongside the scratch dir.

Verified by unit tests in `LmKitOmniApi.Tests/PythonContainerExecutorTests.cs` with a
mock `IProcessRunner` (no real docker) — see the report / §"Tests" below.

---

## GAP 2 — three manual touches in DO-NOT-EDIT files

The option (`CodeInterpreterOptions.JavaScriptEnabled`, default `true`) and the engine
seam (`IExecutionSandboxEngine.IsEnabled => _options.JavaScriptEnabled`, plus a
disabled-invocation guard in `ExecuteCodeSafelyAsync`) are already implemented in the
owned files:

- `Infrastructure/AI/Security/CodeInterpreterOptions.cs` — new `bool JavaScriptEnabled { get; set; } = true;`
- `Infrastructure/AI/Security/IExecutionSandboxEngine.cs` — `bool IsEnabled { get; }` on the
  interface; `ExecutionSandboxEngine` now takes `IOptions<CodeInterpreterOptions>`, exposes
  `IsEnabled`, and returns a safe *"not enabled"* string when off (mirroring the Python path).

Only the three snippets below live in DO-NOT-EDIT files and must be applied by hand.

> **Defense in depth:** even if the orchestrator gate (§3) is *not* applied, a disabled
> invocation is still safe — `ExecuteCodeSafelyAsync` checks `IsEnabled` first and returns
> `"[Sandbox Error] Trình thông dịch JavaScript chưa được bật."` instead of executing. The
> gate only stops the tool from being **offered**.

---

### 1. `appsettings.json` — the JavaScript toggle

`CodeInterpreterOptions` binds from the existing **`CodeInterpreter:Python`** section
(`Program.cs` line 220, `CodeInterpreterOptions.SectionName`). Add the new key **inside
that same block**:

```json
"CodeInterpreter": {
  "Python": {
    "Enabled": false,
    "JavaScriptEnabled": true,
    "Image": "",
    "RuntimePath": "docker",
    "TimeoutSeconds": 15,
    "MemoryMb": 256,
    "Cpus": 1.0,
    "MaxOutputChars": 8000,
    "MaxScriptChars": 20000,
    "MaxOutputFiles": 5,
    "MaxOutputFileBytes": 5242880,
    "MaxTotalOutputFileBytes": 15728640
  }
}
```

- **Exact key:** `CodeInterpreter:Python:JavaScriptEnabled` (the property lives on
  `CodeInterpreterOptions`, whose section is `CodeInterpreter:Python`).
- **Default is `true`** — omitting the key entirely preserves today's behavior (Jint always
  available). Set it to **`false`** to turn `run_javascript` off.

> The audit note refers to this as `CodeInterpreter:JavaScriptEnabled`. Because the option
> object is bound from the `CodeInterpreter:Python` section, the **actual** key is
> `CodeInterpreter:Python:JavaScriptEnabled` — no Program.cs change needed (§2).
>
> *Optional* — if you specifically want the shorter top-level key
> `CodeInterpreter:JavaScriptEnabled`, add a second bind in `Program.cs` next to line 220
> (it layers on top and only fills matching top-level properties):
> ```csharp
> builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.Security.CodeInterpreterOptions>(
>     builder.Configuration.GetSection("CodeInterpreter"));
> ```
> This is not required; the in-block key above is the recommended, lowest-risk choice.

---

### 2. `Program.cs` — NO change required

- **Option binding:** `CodeInterpreterOptions` is *already* bound from `CodeInterpreter:Python`
  (line 220), so the new `JavaScriptEnabled` property is picked up automatically.
- **Engine ctor:** `ExecutionSandboxEngine` is *already* registered (line 214:
  `AddScoped<IExecutionSandboxEngine, ExecutionSandboxEngine>()`). Its constructor now also
  takes `IOptions<CodeInterpreterOptions>`, which DI resolves from the existing `Configure`
  above — no registration edit needed.

(Only apply the *optional* second `Configure` from §1 if you want the top-level config key.)

---

### 3. `Infrastructure/AI/AgentOrchestrator.cs` — gate the tool registration

Today the constructor already receives `IExecutionSandboxEngine executionSandbox` (line 151)
and forwards it to `AgentActionDispatcher` (line 203) but does **not** store it. To gate the
`run_javascript` registration on `IsEnabled` — exactly mirroring how `_pythonExecutor.IsEnabled`
gates `run_python` — apply these three edits (keep the existing forward to the dispatcher):

**3a. Add a field** (next to `_pythonExecutor`, ~line 53):

```csharp
// In-process JavaScript (Jint) interpreter. Held here (like _pythonExecutor) so
// CreateNativeActionToolsAsync can check IsEnabled to decide whether to offer the
// run_javascript tool; the actual execution still runs in the dispatcher via this seam.
private readonly IExecutionSandboxEngine _executionSandbox;
```

**3b. Assign it in the constructor body** (next to `_pythonExecutor = pythonExecutor;`, ~line 178):

```csharp
_executionSandbox = executionSandbox;
```

**3c. The one-line gate** — change the CODE registration (~line 660):

```csharp
// before:
if (ActionAllowed("CODE"))

// after:
if (ActionAllowed("CODE") && _executionSandbox.IsEnabled)
```

> Result: when `JavaScriptEnabled=false`, `run_javascript` is never offered to the agent
> (no error surfaced) — the same shape as `run_python` / `browse_web`. When `true` (default),
> behavior is exactly as before.

---

## Tests (CI-verified, no docker / no model)

- `PythonContainerExecutorTests` — GAP 1: with a mock `IProcessRunner` that simulates the
  docker CLI writing the container id to `--cidfile`:
  - `TimedOut_IssuesDockerKill_WithContainerIdFromCidFile` — a timeout issues a follow-up
    `docker kill <id>` with the id sourced from the cidfile.
  - `Canceled_IssuesDockerKill_ThenPropagates` — the cancellation path also kills, then
    rethrows.
  - `TimedOut_KillDoesNotTake_FallsBackToForceRemove` — a failing kill falls back to
    `docker rm -f <id>`.
  - `SuccessfulRun_IssuesNoDockerKill` / `TimedOut_WithoutContainerId_IssuesNoKill` — a
    normal run (or one with no recorded id) issues **no** kill.
  - `Enabled_HappyPath_PassesCidFileArgument_OutsideTheWorkMount` — `--cidfile` is passed
    and lives outside the `/work` mount source.
- `ExecutionSandboxEngineTests` — GAP 2: `IsEnabled` is `true` by default and `false` when
  `JavaScriptEnabled=false`; a disabled invocation returns the safe *"not enabled"* string
  without executing; the default of `CodeInterpreterOptions.JavaScriptEnabled` is `true`.
