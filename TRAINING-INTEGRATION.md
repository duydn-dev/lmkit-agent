# Grounding fine-tuning pipeline — integration wiring (DO-NOT-EDIT files)

The LoRA **grounding fine-tuning** pipeline captures vetted computer-use steps as supervised
training data → (offline) trains a LoRA adapter from them → registers the produced adapter
through the **existing LoRA hot-swap feature** so it becomes hot-swappable into a custom agent
/ the computer-use loop.

It is built as a self-contained slice under `Infrastructure/AI/ComputerUse/Training/*` plus a
controller (`Controllers/GroundingTrainingController.cs`). It is **OFF BY DEFAULT**: the
recorder writes nothing, the endpoints return **501**, and no training can run until an
operator sets `GroundingTraining:Enabled=true`.

Everything compiles and the full unit-test suite is green **without** touching `Program.cs`,
`appsettings.json`, `Infrastructure/AI/ComputerUse/ComputerUseAgent.cs`, or
`Infrastructure/AI/AgentOrchestrator.cs`. To turn the feature on in a running deployment,
apply the four snippets below by hand.

All new code lives under:
- `LmKitOmniApi/Infrastructure/AI/ComputerUse/Training/GroundingTrainingOptions.cs`
- `LmKitOmniApi/Infrastructure/AI/ComputerUse/Training/GroundingSample.cs` (raw, model-free DTO)
- `LmKitOmniApi/Infrastructure/AI/ComputerUse/Training/IGroundingTraceRecorder.cs` + `FileGroundingTraceRecorder.cs`
- `LmKitOmniApi/Infrastructure/AI/ComputerUse/Training/IGroundingAdapterTrainerPort.cs` (LIVE seam) + `LmKitGroundingAdapterTrainerPort.cs`
- `LmKitOmniApi/Infrastructure/AI/ComputerUse/Training/IGroundingTrainingService.cs` + `GroundingTrainingService.cs`
- `LmKitOmniApi/Controllers/GroundingTrainingController.cs`

### Design constraint that shapes everything

`LMKit.TextGeneration.Chat.ChatHistory` has ONLY `ctor(LM model)` — you cannot build a training
sample without a loaded model. So the **recorder stores a raw, model-free `GroundingSample`
DTO (JSON)**, and **only** the live trainer port (`LmKitGroundingAdapterTrainerPort`, behind
`IGroundingAdapterTrainerPort`) turns those DTOs into `ChatHistory` objects and trains. No
CI-testable code path ever constructs a `ChatHistory`. The training itself
(`LMKit.Finetuning.LoraFinetuning`) is LIVE/OFFLINE — it needs a base model + compute — so it
sits entirely behind that one mockable port; the recorder, the orchestration service, and the
controller are all CI-tested with a fake port and no model.

---

## 1. `Program.cs` — dependency injection

Insert this block **immediately after** the COMPUTER-USE registrations (right after
`builder.Services.AddScoped<…IComputerUseAgent, …ComputerUseAgent>();`, currently **line 272**,
and before `builder.Services.AddScoped<AgentToolGateway>();` on line 274):

```csharp
// Grounding fine-tuning pipeline (disabled by default — see GroundingTrainingOptions).
// Options bound from "GroundingTraining". The recorder persists RAW, model-free samples
// (JSONL); ALL LM-Kit fine-tuning (ChatHistory + LoraFinetuning) is isolated behind
// IGroundingAdapterTrainerPort (LIVE-only), so the recorder/service/controller are unit-tested
// with a fake port and no model. On success the service registers the produced adapter via the
// EXISTING ILoraAdapterService so it becomes hot-swappable (needs Lora:Enabled too). OFF BY
// DEFAULT: the recorder is a no-op and the endpoints return 501.
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.GroundingTrainingOptions>(
    builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.GroundingTrainingOptions.SectionName));
builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.IGroundingTraceRecorder, LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.FileGroundingTraceRecorder>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.IGroundingAdapterTrainerPort, LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.LmKitGroundingAdapterTrainerPort>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.IGroundingTrainingService, LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.GroundingTrainingService>();
```

Lifetimes:
- `FileGroundingTraceRecorder` is a **singleton** — it is a stateless file-appender whose only
  dependencies are `IOptions` + `ILogger` (no captive dependency), and a private semaphore
  serializes appends so JSONL lines never interleave. A scoped `ComputerUseAgent` may depend on
  it (scoped→singleton is fine).
- `LmKitGroundingAdapterTrainerPort` is scoped (it reuses the singleton `LmModelManager`).
- `GroundingTrainingService` is **scoped** because it depends on the scoped `ILoraAdapterService`
  (which wraps the scoped `HermesDbContext`). The controller is per-request, so this is
  scope-compatible.

`ILoraAdapterService` / `LmModelManager` are already registered (LoRA + core wiring), so nothing
else needs to change. The `GroundingTrainingController` is discovered by MVC automatically.

---

## 2. `appsettings.json` — the `"GroundingTraining"` block

Add this section as a sibling of the existing feature blocks — e.g. right **after** the
`"ComputerUse"` block (line 144) and before `"DatabaseAgent"`:

```json
  "GroundingTraining": {
    "Enabled": false,
    "DatasetPath": "",
    "AdapterOutputPath": "",
    "MinSamplesToTrain": 50,
    "Rank": 8,
    "Alpha": 16,
    "Epochs": 1,
    "LearningRate": 0.0001
  },
```

- `Enabled: false` is the master off switch (recorder no-ops, endpoints 501).
- `DatasetPath: ""` → defaults to `<current-dir>/App_Data/grounding` at runtime; a per-tenant
  subdirectory (`<tenantId:N>/samples.jsonl`) is created under it. Point it at a persistent
  volume in prod.
- `AdapterOutputPath: ""` → defaults to `<DatasetPath>/adapters` (a per-tenant subdirectory is
  created under it) for the trained `.gguf` before it is registered.
- `MinSamplesToTrain: 50` → `run` refuses (409) until at least this many vetted samples exist.
- `Rank` / `Alpha` / `Epochs` / `LearningRate` → LoRA training knobs passed to the trainer port.

To actually train + register an adapter you need **both** `GroundingTraining:Enabled=true`
**and** `Lora:Enabled=true` (registration goes through the LoRA hot-swap service). With
`GroundingTraining` on but `Lora` off, a run still trains the adapter file but reports
`TrainedNotRegistered` (HTTP 200, `registered:false`) instead of registering it.

---

## 3. `Infrastructure/AI/ComputerUse/ComputerUseAgent.cs` — the recorder hook

This is the capture point: **after a side-effecting action has been human-approved (or, when
per-action approval is off, allowed) AND executed WITHOUT error**, that step is a human-vetted
correct label — record it as one supervised `GroundingSample`. The training INPUT is the page
the model *saw when it decided* (the pre-action observation), and the LABEL is the action JSON.

`ComputerUseAgent.cs` is on the DO-NOT-EDIT list, so apply these four edits by hand.

### 3a. Constructor — inject the recorder (optional, so existing call sites still compile)

Add the field near the other collaborator fields (by `_audit`):

```csharp
private readonly LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.IGroundingTraceRecorder? _groundingRecorder;
```

Add a **trailing optional** parameter to the constructor (after `AgentToolAuditService? audit = null`)
and assign it — trailing + optional so the existing unit tests that construct the agent
positionally keep compiling, and DI fills it by type:

```csharp
public ComputerUseAgent(
    IComputerUseExecutor executor,
    IComputerUseModel model,
    IComputerUseApprovalGate approvalGate,
    IOptions<ComputerUseOptions> options,
    UserResourceAccessService resources,
    ToolSandboxService sandbox,
    ILogger<ComputerUseAgent> logger,
    AgentToolAuditService? audit = null,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.IGroundingTraceRecorder? groundingRecorder = null) // ← ADD
{
    _executor = executor;
    _model = model;
    _approvalGate = approvalGate;
    _options = options.Value;
    _resources = resources;
    _sandbox = sandbox;
    _logger = logger;
    _audit = audit;
    _groundingRecorder = groundingRecorder; // ← ADD
}
```

### 3b. Capture the PRE-action observation (before it is overwritten by the step result)

In `RunAsync`, in the main perception→action loop, find the `// ── Execute ──` block
(currently **line 259**). **Immediately before** the `// ── Execute ──` comment, capture the
observation the model actually decided against (it is reassigned by the step on line 262):

```csharp
// GROUNDING TRAINING (off by default): remember the page the model SAW when it chose this
// already-approved, allowlisted action, so a successful side-effecting step can be captured
// below as a vetted supervised sample (input = pre-action page, label = the action JSON).
var groundingPreObservation = observation;

// ── Execute ──
var (stepObs, stepCancelled) = await TryStepAsync(action, request, sessionDir, sct);
```

### 3c. Record the sample after a successful side-effecting step

Right **after** `TrimHistory(history);` (currently **line 266**, inside that same Execute block),
add the capture call. It awaits a plain `Task` (no `yield` inside), so it is legal in the
iterator's `try/finally`:

```csharp
history.Add(Summarize(action, observation));
TrimHistory(history);

// GROUNDING TRAINING (off by default): a side-effecting action that was approved AND executed
// WITHOUT error is a vetted correct step — capture it as one supervised grounding sample.
// Best-effort; never breaks the loop; a no-op unless GroundingTraining:Enabled.
if (_groundingRecorder?.Enabled == true && action.IsSideEffecting && !observation.IsError)
    await CaptureGroundingSampleAsync(request, action, groundingPreObservation);
```

(Only the model-decided actions in the loop are captured; the optional initial navigation to
the user-supplied `StartUrl` is the user's own input, not a model grounding decision, so it is
intentionally left uncaptured.)

### 3d. Add the private helpers

Add these three private methods to `ComputerUseAgent` (it already has `using System.Text;` and
`using System.Text.Json;`). `SystemPrompt` and `_options` are already members:

```csharp
private async Task CaptureGroundingSampleAsync(
    ComputerUseRequest request, ComputerUseAction action, ComputerUseObservation preObservation)
{
    try
    {
        var sample = new LmKitOmniApi.Infrastructure.AI.ComputerUse.Training.GroundingSample
        {
            TenantId = request.TenantId,
            TaskGoal = request.TaskGoal,
            PageUrl = preObservation.Url,
            ElementsText = RenderGroundingElements(preObservation),
            ScreenshotFileId = preObservation.ScreenshotFileId,
            SystemPrompt = SystemPrompt,
            CorrectActionJson = ToGroundingActionJson(action),
            Source = _options.RequireApprovalPerAction ? "approved" : "success",
        };
        await _groundingRecorder!.RecordAsync(sample, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "🧪 [ComputerUse] Không ghi được mẫu huấn luyện grounding (không nghiêm trọng).");
    }
}

/// <summary>Renders the numbered element list EXACTLY as the model saw it (mirrors ComputerUseModel).</summary>
private static string RenderGroundingElements(ComputerUseObservation observation)
{
    var sb = new StringBuilder();
    sb.Append("INTERACTIVE ELEMENTS (address these by 'ref'):\n");
    if (observation.Elements.Count == 0)
    {
        sb.Append("  (none detected)\n");
    }
    else
    {
        foreach (var el in observation.Elements)
        {
            sb.Append("  [").Append(el.Ref).Append("] ").Append(el.Role).Append(": ").Append(el.Name);
            if (!string.IsNullOrEmpty(el.Value)) sb.Append(" = \"").Append(el.Value).Append('"');
            sb.Append('\n');
        }
    }
    return sb.ToString();
}

/// <summary>The supervised LABEL: the vetted action as the canonical JSON the model should emit.</summary>
private static string ToGroundingActionJson(ComputerUseAction a) => a.Type switch
{
    ComputerUseActionType.Navigate => JsonSerializer.Serialize(new { action = "navigate", url = a.Url }),
    ComputerUseActionType.Click => a.Ref is int r
        ? JsonSerializer.Serialize(new { action = "click", @ref = r })
        : JsonSerializer.Serialize(new { action = "click", x = a.X, y = a.Y }),
    ComputerUseActionType.Type => JsonSerializer.Serialize(new { action = "type", @ref = a.Ref, text = a.Text }),
    ComputerUseActionType.Key => JsonSerializer.Serialize(new { action = "key", keys = a.Keys }),
    _ => JsonSerializer.Serialize(new { action = a.Type.ToString().ToLowerInvariant() }),
};
```

> **Why this is the correct label.** Only `navigate` (allowlist + SSRF gated) and grounded
> `click`/`type`/`key` (a `ref` resolvable in the current observation) ever reach "executed
> without error" — the loop's fail-closed gates refuse credential/CAPTCHA fields and
> un-groundable actions *before* execution, and the approval gate blocks anything a human did
> not approve. So every captured step is a real, safe, human-vetted grounding decision.
>
> **Privacy note.** A captured `type` label includes the typed `text`. Credential/payment/OTP
> fields are already refused upstream, and the dataset is off by default, tenant-scoped, and
> local — but if you enable it, treat `App_Data/grounding` as sensitive and consider redacting
> free-text before training.

---

## 4. Endpoints

All endpoints require authentication (`[Authorize]`) and are tenant-scoped from the JWT claims
(never the body). The whole feature is off by default.

| Method & route | Auth | Purpose |
| --- | --- | --- |
| `GET /api/computer-use/grounding-training/stats` | any user | `{ enabled, sampleCount }` for the caller's tenant. **501 when disabled.** |
| `POST /api/computer-use/grounding-training/run` | **Admin** | Train a grounding adapter from the tenant's vetted samples and register it via LoRA hot-swap. **501 when disabled**, **409** when `< MinSamplesToTrain`, **500** when training fails, **200** on success (`{ registered:true, adapterId, adapterPath, sampleCount }`) or trained-but-unregistered (`{ registered:false, message, adapterPath, sampleCount }` when `Lora:Enabled` is off). |

## 5. What is CI-verified vs live-only

**CI-verified** (20 tests, no model / no compute / no container):
- `GroundingTraceRecorderTests` — the record→read roundtrip (all fields), append order, the
  `Enabled=false` no-op (nothing written to disk), and tenant isolation, against a real temp dir.
- `GroundingTrainingServiceTests` — orchestration with a FAKE `IGroundingAdapterTrainerPort` and
  a fake `ILoraAdapterService`: refuses when disabled and when below `MinSamplesToTrain` (port
  and registration never touched), and on success calls the port **THEN** registers the produced
  adapter (order asserted via a shared call log; the exact produced file is what gets
  registered). Plus `TrainedNotRegistered` when the LoRA feature is off.
- `GroundingTrainingServiceRegistrationTests` — the REAL `LoraAdapterService` (in-memory SQLite
  + a fake `ILoraModelPort`, LoRA feature ON) actually turns the produced adapter file into a
  tenant-scoped, hot-swappable registration row.
- `GroundingTrainingControllerTests` — `501` on both endpoints when off, the outcome→HTTP mapping
  (200 / 409 / 500), unauthorized without identity, and the Admin-only `run` surface.

**Live-only** (needs the real stack; exercised in a running deployment):
- `LmKitGroundingAdapterTrainerPort` — the actual `ChatHistory` construction + `LoraFinetuning`
  training run against a loaded chat model (the one place LM-Kit fine-tuning is touched).
- The `ComputerUseAgent` recorder hook capturing real vetted steps during a live computer-use run.

## Quick verification after wiring

1. `dotnet build LmKitOmniApi/LmKitOmniApi.csproj -c Debug`
2. `dotnet test LmKitOmniApi.Tests/LmKitOmniApi.Tests.csproj -c Debug`
3. With `GroundingTraining:Enabled=false` (default): `GET …/grounding-training/stats` → `501`;
   `POST …/grounding-training/run` (Admin) → `501`.
4. With `GroundingTraining:Enabled=true` + `Lora:Enabled=true`: run a computer-use session and
   approve some side-effecting steps → `GET …/stats` shows a rising `sampleCount`; once it
   reaches `MinSamplesToTrain`, `POST …/run` (Admin) trains an adapter and returns
   `{ registered:true, adapterId, … }`. That adapter then appears in `GET /api/lora-adapters`
   and can be bound to a custom agent via `POST /api/lora-adapters/{id}/assign?agentId=…`.
