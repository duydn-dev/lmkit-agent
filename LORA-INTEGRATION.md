# LoRA hot-swap — integration wiring (DO-NOT-EDIT files)

The LoRA hot-swap feature is fully built and tested in this worktree, but four files are
owned by the coordinator and were **not** edited here. Apply the snippets below to wire the
feature in. The whole feature is **OFF BY DEFAULT** — until `Lora:Enabled` is `true` the
mutation endpoints return 501 and the orchestrator never applies an adapter, so applying
these snippets is behavior-preserving.

All new code lives under:
- `LmKitOmniApi/Domain/Entities/LoraAdapterRegistration.cs` (+ `CustomAgent.LoraAdapterId`)
- `LmKitOmniApi/Infrastructure/AI/Lora/*` (options, port, service, scope, exceptions)
- `LmKitOmniApi/Application/LoraAdapters/*` (rules, DTO, commands, queries, handlers)
- `LmKitOmniApi/Controllers/LoraAdaptersController.cs`
- `LmKitOmniApi/Migrations/20260904142258_AddLoraAdapters.cs` (+ Designer + snapshot)

`AgentRequestOptions.LoraAdapterId` (nullable `Guid?`) is **already added** in
`Application/Abstractions/AgentRequestOptions.cs` (that file is not on the DO-NOT-EDIT list),
so step 4 only has to *populate* it.

---

## 1. `Program.cs` — DI registration

Insert these three lines immediately **after** the WebRead registration block (right after
`builder.Services.AddScoped<IWebReadService, LmKitWebReadService>();`, currently ~line 242,
before `builder.Services.AddScoped<AgentToolGateway>();`):

```csharp
// LoRA hot-swap (disabled by default — see LoraOptions). Options bound from "Lora".
// The LM-Kit ApplyLoraAdapter/RemoveLoraAdapter calls are isolated behind ILoraModelPort so
// the service + everything above it is unit-tested with a fake port and no native model.
// When disabled, RegisterAsync refuses (501) and BeginApplyForAgent is a no-op.
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.Lora.LoraOptions>(
    builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.Lora.LoraOptions.SectionName));
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Lora.ILoraModelPort, LmKitOmniApi.Infrastructure.AI.Lora.LmKitLoraModelPort>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Lora.ILoraAdapterService, LmKitOmniApi.Infrastructure.AI.Lora.LoraAdapterService>();
```

Lifetimes: both are `Scoped` (the service wraps the scoped `HermesDbContext`). This matches
`AgentOrchestrator` (also `AddScoped`, ~line 449), so injecting `ILoraAdapterService` into it
is scope-compatible.

The MediatR handlers under `Application/LoraAdapters/Handlers/*` are auto-registered by the
existing `AddMediatR(...)` assembly scan and need no explicit registration — but they depend
on `ILoraAdapterService`, so they only resolve once the three lines above are present. (This
is why the three registrations are required for the API endpoints to work.)

---

## 2. `appsettings.json` — the `"Lora"` block

Add this section as a sibling of the other feature sections (e.g. right after `"WebRead"`
or `"DatabaseAgent"`):

```json
  "Lora": {
    "Enabled": false,
    "AdapterStoragePath": "",
    "MaxAdapterBytes": 536870912,
    "DefaultScale": 1.0,
    "MinScale": 0.0,
    "MaxScale": 2.0
  },
```

- `Enabled: false` is the master off switch.
- `AdapterStoragePath: ""` → defaults to `<current-dir>/App_Data/lora` at runtime (a
  per-tenant subdirectory is created under it). Point it at a persistent volume in prod.
- `MaxAdapterBytes: 536870912` = 512 MiB.
- `MinScale`/`MaxScale` bound (and clamp) the applied scale; `DefaultScale` is used when a
  registration omits one.

---

## 3. `Infrastructure/AI/AgentOrchestrator.cs` — apply the adapter around chat inference

**3a. Constructor injection.** Add a field and a constructor parameter (anywhere in the long
parameter list is fine):

```csharp
// field, near the other "// ── Core ──" fields:
private readonly LmKitOmniApi.Infrastructure.AI.Lora.ILoraAdapterService _loraService;

// new constructor parameter:
LmKitOmniApi.Infrastructure.AI.Lora.ILoraAdapterService loraService,

// assignment in the constructor body:
_loraService = loraService;
```

**3b. Apply around inference.** In `StreamProcessQueryAsync`, the chat inference lease is
acquired at (currently) **line 253**:

```csharp
await using var inferenceLease = await _modelManager.AcquireChatInferenceAsync(cancellationToken);
```

Insert the two lines below **immediately after** that line (and before
`_telemetry.RecordReActIteration(...)` / `ExecuteNativeReActAsync(...)`):

```csharp
// LoRA hot-swap: apply the agent's adapter to the shared chat model for the whole
// inference (ReAct tool pass + synthesis pass), then remove it before the lease is
// released. `using` disposes loraScope BEFORE inferenceLease (reverse declaration order),
// i.e. the adapter is removed while we still hold the lease — and even if inference throws.
// BeginApplyForAgent returns null (a no-op) when the feature is off, no adapter is bound,
// or the registration is missing/inactive/file-gone, so this is safe unconditionally.
var loraModel = await _modelManager.GetChatModelAsync(ct: cancellationToken);
using var loraScope = _loraService.BeginApplyForAgent(loraModel, tenantId, options?.LoraAdapterId, cancellationToken);
```

Why here: `tenantId` and `options` are already method parameters; the same cached `LM`
instance is used by both the ReAct pass (`ExecuteNativeReActAsync`) and the synthesis pass
(`chat.Submit`), so applying once at the top covers both. Disposal order guarantees removal
happens after `chat.Submit` has fully unwound (the existing `llmThreadDone` await in the
consumer-loop `finally` runs first) and before `inferenceLease` is released.

Optional micro-optimization (avoids an early model fetch when no adapter is bound):

```csharp
using var loraScope = options?.LoraAdapterId is null
    ? null
    : _loraService.BeginApplyForAgent(
        await _modelManager.GetChatModelAsync(ct: cancellationToken), tenantId, options.LoraAdapterId, cancellationToken);
```

No other method in this file needs changes. The approved-resume path
(`ExecuteDirectActionAsync`) runs single tool actions with `options: null`, so it carries no
adapter — leave it as is.

---

## 4. `Application/Chat/Handlers/StreamChatCommandHandler.cs` — populate `LoraAdapterId`

In `BuildAgentOptionsAsync`, the custom-agent branch builds `AgentRequestOptions` (currently
~lines 488–498). Add the one property so a bound agent's adapter flows into the request:

```csharp
options = new AgentRequestOptions
{
    AllowWebSearch = request.EnableWebSearch
        && (allowedTools is null || allowedTools.Contains("SearchWeb", StringComparer.OrdinalIgnoreCase)),
    PersonaPrompt = customAgent.PersonaPrompt,
    AllowedTools = allowedTools,
    KnowledgeDocumentIds = CustomAgentRules.ParseDocumentIdsCsv(customAgent.KnowledgeDocumentIdsCsv),
    LoraAdapterId = customAgent.LoraAdapterId   // ← ADD THIS LINE
};
```

The agentless branch (`new AgentRequestOptions { AllowWebSearch = request.EnableWebSearch }`)
leaves `LoraAdapterId` null, which is correct — an unbound session applies no adapter. The
later `with { ... }` compositions (project instructions, user preferences, reasoning) preserve
`LoraAdapterId` because `with` copies unlisted properties.

`AgentRequestOptions.LoraAdapterId` already exists (added in this worktree) — no change to
`AgentRequestOptions.cs` is needed here.

---

## Migration

`dotnet ef migrations add AddLoraAdapters` was already run in this worktree
(`20260904142258_AddLoraAdapters`), adding the `lora_adapter_registrations` table (unique
index on `(TenantId, Name)`, index on `(TenantId, IsActive)`) and the nullable
`custom_agents.LoraAdapterId` column, and updating `HermesDbContextModelSnapshot.cs`. It
applies with the existing migration flow (`Database:ApplyMigrations`). Tests use
`EnsureCreated()` against SQLite, so they exercise the model directly and do not depend on the
Npgsql migration.

---

## Quick verification after wiring

1. `dotnet build LmKitOmniApi/LmKitOmniApi.csproj -c Debug`
2. `dotnet test LmKitOmniApi.Tests/LmKitOmniApi.Tests.csproj -c Debug`
3. With `Lora:Enabled=false` (default): `GET /api/lora-adapters` → `200 []`;
   `POST /api/lora-adapters` (Admin) → `501`.
4. With `Lora:Enabled=true`: Admin `POST` a valid adapter (multipart `file` + `name`) → `201`;
   bind it to an owned custom agent via `POST /api/lora-adapters/{id}/assign?agentId=...`; a
   chat with that agent applies the adapter for the turn and removes it afterwards.

## Endpoint summary (`api/lora-adapters`, all `[Authorize]`)

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| GET | `/` | any user | tenant-scoped list; `[]` when disabled |
| GET | `/{id}` | any user | 404 when missing |
| POST | `/` | **Admin** | multipart `file` + `name`/`description`/`scale`/`targetModelId`; 501 when disabled |
| PUT | `/{id}` | **Admin** | body `{ name?, scale?, isActive? }`; 501/404/400 |
| DELETE | `/{id}` | **Admin** | deletes row + file; 501/404 |
| POST | `/{id}/assign?agentId=` | any user (owner-scoped) | binds adapter to caller's own agent |
| DELETE | `/assign?agentId=` | any user (owner-scoped) | clears the agent's adapter |

Security: adapter files are Admin-uploaded only, stored under a server-generated,
tenant-scoped path (never the client file name), format-validated before persistence, and
size-capped while streaming to disk.
