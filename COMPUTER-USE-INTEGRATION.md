# Computer-Use Agent — Integration Guide

The interactive **computer-use** agent (perception→action loop that drives a real browser:
`navigate` / `click` / `type` / `key` / `scroll` / `wait` / `screenshot` / `done` / `ask`) is
built as a self-contained slice under `Infrastructure/AI/ComputerUse/*`, a controller
(`Controllers/ComputerUseController.cs`), and a MediatR command
(`Application/ComputerUse/*`).

It is **OFF BY DEFAULT** and is the single most safety-sensitive feature in the product,
so every side-effecting action is gated on human approval, navigation is restricted to an
**explicit allowlist** (empty = deny-all), and credentials/CAPTCHAs are refused and handed
off to a human.

## DO NOT EDIT — apply these snippets manually

Per the build constraints, this feature ships **without touching** `Program.cs`,
`appsettings.json`, `Infrastructure/AI/AgentOrchestrator.cs`,
`Infrastructure/AI/Tools/AgentActionDispatcher.cs`, or
`Infrastructure/AI/Security/ToolPermissionService.cs`. Everything compiles, the full test
suite is green, and the controller + services are unit-tested **without** these edits.

To actually turn the feature on in a running deployment, an operator applies the snippets
below. The MediatR resolve handler (`ResolveComputerUseApprovalCommandHandler`) is picked
up automatically by the existing assembly scan
(`AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly))`), so only
the DI registrations, the config block, and (optionally) the tool-graph wiring are manual.

---

## 1. `Program.cs` — dependency injection

Add this block next to the existing Python/Browser executor registrations (after the
`BrowserFetchOptions` block, ~line 233). It reuses the already-registered singleton
`IProcessRunner`, scoped `ToolSandboxService` / `UserResourceAccessService`, scoped
`AgentToolAuditService`, singleton `TaskApprovalPayloadProtector`, and singleton
`LmModelManager` — so nothing else needs to change.

```csharp
// Interactive COMPUTER-USE agent (disabled by default — see ComputerUseOptions).
// Options bound from "ComputerUse". Reuses the shared IProcessRunner seam; every
// collaborator is a seam so the loop is unit-testable without a container/model.
// OFF BY DEFAULT: when disabled the executor/agent report IsEnabled=false and the
// controller returns 501. Navigation is restricted to an EXPLICIT allowlist
// (empty = deny-all) plus the SSRF gate; every side-effecting action is HITL-gated.
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseOptions>(
    builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseOptions.SectionName));
builder.Services.AddScoped<
    LmKitOmniApi.Infrastructure.AI.ComputerUse.IComputerUseExecutor,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseExecutor>();
builder.Services.AddScoped<
    LmKitOmniApi.Infrastructure.AI.ComputerUse.IComputerUseModel,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseModel>();
builder.Services.AddScoped<
    LmKitOmniApi.Infrastructure.AI.ComputerUse.IComputerUseApprovalGate,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseApprovalGate>();
builder.Services.AddScoped<
    LmKitOmniApi.Infrastructure.AI.ComputerUse.IComputerUseAgent,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.ComputerUseAgent>();
```

> The controller (`ComputerUseController`) is discovered by MVC automatically. Until the
> registrations above are added, a request to `/api/agent/computer-use` cannot resolve
> `IComputerUseAgent` — which is fine, because the feature is meant to be off until an
> operator deliberately wires and enables it.

---

## 2. `appsettings.json` — the `"ComputerUse"` block

Add a top-level `"ComputerUse"` section (sibling of the existing `"BrowserTool"` /
`"CodeInterpreter"` blocks). Everything defaults to the safe state; `Enabled:false` and an
empty `AllowedHosts` mean nothing can run or navigate until an operator opts in.

```json
"ComputerUse": {
  "Enabled": false,
  "Image": "",
  "RuntimePath": "docker",
  "NetworkName": "",
  "AllowedHosts": [],
  "MaxSteps": 15,
  "StepTimeoutSeconds": 30,
  "SessionWallClockSeconds": 300,
  "MemoryMb": 512,
  "Cpus": 1.0,
  "PidsLimit": 512,
  "MaxScreenshotBytes": 5242880,
  "MaxElements": 100,
  "RequireApprovalPerAction": true,
  "ApprovalTimeoutSeconds": 300
}
```

To enable it for real:
- set `Enabled: true`;
- set `Image` to a hardened interactive-browser container image (contract in §5);
- add **every** host the agent may visit to `AllowedHosts` (empty stays deny-all);
- ideally set `NetworkName` to an operator-provisioned, egress-restricted container network
  (firewall/proxy) so per-host egress is enforced at the network layer, not just at the
  pre-navigation check;
- keep `RequireApprovalPerAction: true` unless you fully understand the consequences of
  auto-executing browser actions.

---

## 3. (Optional) Expose it as a ReAct agent action — tool-graph / permission wiring

The standalone controller does **not** need any of this: the loop enforces approval,
allowlist, step cap, and the credential/CAPTCHA refusal on its own. Apply this section
**only** if you also want the ReAct orchestrator to be able to trigger a computer-use run
as a tool (action name `COMPUTER_USE` → tool `UseComputer`). These live in the DO-NOT-EDIT
files, so they are given as exact snippets to apply by hand.

**3a. `Infrastructure/AI/AgentOrchestrator.cs` — add to `ActionToToolMap`:**

```csharp
["COMPUTER_USE"] = "UseComputer",
```

**3b. `Infrastructure/AI/Security/ToolPermissionService.cs`:**

Add `"UseComputer"` to the `Admin` **and** `User` role sets (never `Guest`):

```csharp
// in RoleToolPermissions["Admin"] and RoleToolPermissions["User"]:
"UseComputer", // interactive computer-use — networked, side-effecting; approval-required below
```

Add it to `ApprovalRequiredTools` (it is side-effecting egress, so it always needs a human):

```csharp
// in ApprovalRequiredTools:
"UseComputer",
```

Give it a **tight** rate limit — on par with the most sensitive tools (code exec / browse):

```csharp
// in ToolRateLimits:
["UseComputer"] = 3,   // per minute, per user — the most powerful tool gets the tightest cap
```

> Result: `UseComputer` is available to Admin/User only, is approval-required, and is rate
> limited to 3/min/user — matching how `BrowseWeb` / `RunPython` are treated, but tighter.

---

## 4. Endpoints

All endpoints require authentication (`[Authorize]`) and are tenant-scoped from the JWT.
The streaming endpoint is role-gated to **Admin/User** (never Guest) and rate-limited with
the shared `ai-agent` policy.

| Method & route | Purpose |
| --- | --- |
| `POST /api/agent/computer-use` | Start a run. Body: `{ "task": "...", "startUrl": "https://..." }` (`startUrl` optional). Streams SSE. **Returns `501` when the tool is disabled.** |
| `POST /api/agent/computer-use/approvals/{id}/approve` | Approve the pending action so the streaming loop proceeds. |
| `POST /api/agent/computer-use/approvals/{id}/reject` | Reject the pending action (optional body `{ "comment": "..." }`); the loop stops. |

**SSE marker channel** (same channel as chat, so the SPA renders it):
- `[COMPUTER_USE:{sessionId}]` — first event, correlates the run + its approvals.
- `[THINKING]: …` — human-readable progress.
- `[STEP:{ordinal,action,input,observation}]` — one per executed step.
- `[FILE:{id,name,contentType,size}]` — the per-step screenshot (served owner-scoped from
  `GET /api/files/{id}`).
- `[HITL_APPROVAL_REQUIRED:{approvalId}]` — emitted **before** each gated action; the loop
  then blocks on the approval gate. Resolve it with the approve/reject endpoints above.
- `[DONE]` — end of stream.

### Approval flow (why a dedicated resolve path)

A computer-use action **executes inside the streaming loop**, not through the generic tool
dispatcher. So approvals are recorded on the existing `TaskApproval` table (owner-scoped,
encrypted payload, visible in the normal pending-approvals list) but resolved through the
dedicated endpoints, which only flip the row's status. `ComputerUseApprovalGate` polls that
status and proceeds **only** on an explicit `Approved`; a rejection, a timeout
(`ApprovalTimeoutSeconds`), or a disconnect all **fail closed** (the action is not executed).
Do **not** use the generic `POST /api/TaskApproval/{id}/approve` for these — it routes
`COMPUTER_USE` through the dispatcher (which has no such tool) and fails closed by design.

---

## 5. Container image contract (for the `Image` you provision)

Execution is **live-only** — it needs a real interactive-browser image. Each step the
executor runs one hardened, ephemeral container:

```
docker run --rm --interactive=false \
  --memory 512m --memory-swap 512m --cpus 1.0 --pids-limit 512 \
  --user 65534:65534 --cap-drop ALL --security-opt no-new-privileges \
  --read-only --tmpfs /tmp:rw,size=256m,noexec \
  [--network <NetworkName>] \
  --env COMPUTER_USE_ALLOWED_HOSTS=<csv> \
  --workdir /session --volume <hostSessionDir>:/session:rw \
  <Image> --action '<actionJson>'
```

- **Input:** the action as a JSON string in the `--action` argument, e.g.
  `{"action":"click","ref":3}`, `{"action":"navigate","url":"https://…"}`,
  `{"action":"type","ref":2,"text":"…"}`.
- **Persistent profile:** the container reads/writes the browser profile under `/session`
  (mounted from a per-run host dir), so cookies/page/scroll carry across the run's steps.
- **Output:** a single-line **observation JSON on stdout**:
  ```json
  {"url":"https://…","title":"…",
   "elements":[{"ref":1,"role":"link","name":"Home","value":null}],
   "screenshot":"step.png","error":null}
  ```
  `screenshot` is a filename the container wrote under `/session`; the executor harvests it
  into the caller's isolated upload root (server-generated name, byte-capped by
  `MaxScreenshotBytes`) and surfaces it as `ScreenshotFileId`. Set `error` to a string to
  report a step failure without a non-zero exit.
- The image **must** honour `COMPUTER_USE_ALLOWED_HOSTS` as an in-container guard, and the
  operator **should** additionally constrain egress at the network layer via `NetworkName`.

---

## 6. Safety rails (enforced in code)

1. **Off by default** (`Enabled=false`) — controller returns `501`, executor/agent report
   `IsEnabled=false`.
2. **Navigation allowlist** — `AllowedHosts` is an **explicit** allowlist; **empty = deny
   all** (stricter than the read-only browse tool). Enforced in both the agent loop and the
   executor.
3. **SSRF gate** — every `navigate` is validated by `ToolSandboxService.ValidateUrlAsync`
   (host + every resolved IP) before any container launches; internal/loopback/link-local/
   metadata targets are always blocked.
4. **HITL approval per action** — every side-effecting action (navigate/click/type/key) is
   gated on human approval when `RequireApprovalPerAction=true`; read-only actions
   (screenshot/scroll/wait/done/ask) never are. The gate fails closed.
5. **Credential / CAPTCHA refusal** — `ComputerUseSafetyGuard` refuses typing into
   credential/payment fields and interacting with CAPTCHA controls; such steps are handed
   off to the human via `ask` and are **never executed** and **never even sent to the
   approval gate**. The same rule is stated in the system prompt. The agent never attempts
   to create accounts, log in, enter passwords/payment details, or solve bot-detection.
6. **Container isolation** — non-root (`65534:65534`), `--cap-drop ALL`,
   `--security-opt no-new-privileges`, read-only rootfs + noexec tmpfs, memory==memory-swap
   (swap off), cpu/pids caps.
7. **Step cap + per-session wall-clock cap** — `MaxSteps` and `SessionWallClockSeconds` bound
   every run.
8. **Audit** — every action is recorded through `AgentToolAuditService` (tool name
   `COMPUTER_USE`), and every gated action leaves an owner-scoped `TaskApproval` row.

---

## 7. What is CI-verified vs live-only

**CI-verified** (48 tests, no container / no model / no DB):
- `ComputerUseActionParserTests` — tolerant JSON parsing (fences/prose/wrappers/aliases/
  string-numbers, balanced-brace extraction) and safe rejection of malformed/unknown/
  under-specified actions.
- `ComputerUseExecutorTests` — hardened `docker run` construction (non-root, cap-drop,
  no-new-privileges, read-only, memory/pids, net-allowlist via `--network` + the
  `COMPUTER_USE_ALLOWED_HOSTS` env), SSRF pre-validation firing before any launch,
  strict deny-all allowlist, observation parsing, screenshot harvesting + byte cap, and the
  timeout/non-zero-exit/malformed failure modes — all via a mock `IProcessRunner`.
- `ComputerUseAgentTests` — the loop: observe→decide→approval→act→terminate on `done`; the
  step cap; refusal of non-allowlisted navigation (before approval and before the executor);
  an unapproved side-effecting action is **not** executed; and credential/CAPTCHA actions are
  handed off, never executed, never gated — all with a fake executor + scripted model +
  scripted approver.
- `ComputerUseControllerTests` — `501` when disabled, `400` on empty task, `403` for a
  non-Admin/User role, and SSE headers on the enabled path.

**Live-only** (needs the real stack; exercised in a running deployment):
- `ComputerUseExecutor` actually launching a browser container and its persistent
  `/session` profile (the wire contract in §5).
- `ComputerUseModel` — the real vision-model inference via `LmModelManager`.
- `ComputerUseApprovalGate` — the database-backed pending-approval creation + polling and the
  approve/reject resolution endpoints.
