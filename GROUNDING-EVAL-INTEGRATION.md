# Grounding Evaluation Harness — Integration Guide

The **grounding evaluation harness** measures how well the computer-use model *grounds* its
next action: given a fixed observation (url / title / numbered interactive elements) and a
task goal, does it pick a **valid, correct element `ref`**? It classifies each decision into
one of five buckets — **Malformed / NonElement / Hallucinated / ValidButWrong / Correct** —
and reports `GroundingAccuracy` (Correct / Total) and `HallucinationRate` (Hallucinated /
Total), so model / prompt / grammar tuning has a concrete target.

It is a **pure diagnostic**: it only asks the model what it *would* decide (reusing
`ComputerUseAgent.SystemPrompt` and `ComputerUseActionParser`, exactly like the live loop).
It never opens a browser, executes an action, navigates, or requires approval.

It is built as a self-contained slice under `Infrastructure/AI/ComputerUse/Eval/*` plus a
controller (`Controllers/GroundingEvalController.cs`).

## Off by default

The harness is **disabled by default**. When off, `IGroundingEvaluator.IsEnabled` reports
`false`, `GroundingEvaluator.EvaluateAsync` refuses (throws `InvalidOperationException`), and
`POST /api/computer-use/grounding-eval` returns **`501 Not Implemented`**.

## DO NOT EDIT — apply these snippets manually

Per the build constraints, this feature ships **without touching** `Program.cs`,
`appsettings.json`, or `Infrastructure/AI/AgentOrchestrator.cs`. Everything compiles and the
full test suite is green without those edits. To turn the harness on in a running
deployment, an operator applies the two snippets below.

> **Dependency:** the evaluator resolves `IComputerUseModel` (the same model seam the
> computer-use loop uses). That interface is registered by the computer-use DI block in
> `COMPUTER-USE-INTEGRATION.md` §1 (`AddScoped<IComputerUseModel, ComputerUseModel>()`). So
> the harness requires the computer-use registrations to be present as well; enabling the
> harness (`GroundingEval:Enabled=true`) does **not** require enabling the computer-use tool
> itself (`ComputerUse:Enabled`) — only that `IComputerUseModel` is registered.

---

## 1. `Program.cs` — dependency injection

Add this block next to the computer-use executor registrations (see
`COMPUTER-USE-INTEGRATION.md` §1). It binds the options from the `"GroundingEval"` section
and registers the evaluator; it reuses the already-registered `IComputerUseModel`.

```csharp
// Grounding EVALUATION harness (disabled by default — see GroundingEvalOptions).
// Options bound from "GroundingEval". Reuses the IComputerUseModel seam registered with the
// computer-use tool, so it is unit-testable with a scripted model and needs no container.
// OFF BY DEFAULT: when disabled the evaluator reports IsEnabled=false, EvaluateAsync throws,
// and the controller returns 501.
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval.GroundingEvalOptions>(
    builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval.GroundingEvalOptions.SectionName));
builder.Services.AddScoped<
    LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval.IGroundingEvaluator,
    LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval.GroundingEvaluator>();
```

> The controller (`GroundingEvalController`) is discovered by MVC automatically. Until the
> registrations above are added, a request to `/api/computer-use/grounding-eval` cannot
> resolve `IGroundingEvaluator` — which is fine, because the feature is meant to be off until
> an operator deliberately wires and enables it.

---

## 2. `appsettings.json` — the `"GroundingEval"` block

Add a top-level `"GroundingEval"` section (sibling of the existing `"ComputerUse"` block).
It defaults to the safe state; `Enabled:false` means the harness cannot run.

```json
"GroundingEval": {
  "Enabled": false
}
```

To enable it, set `"Enabled": true` (and ensure the computer-use DI block from
`COMPUTER-USE-INTEGRATION.md` §1 is present so `IComputerUseModel` resolves).

---

## 3. Endpoint

Authenticated (`[Authorize]`), **Admin-only** (`[Authorize(Roles = "Admin")]`), and
rate-limited with the shared `ai-agent` policy.

| Method & route | Purpose |
| --- | --- |
| `POST /api/computer-use/grounding-eval` | Run the harness and return the report. Body is optional (see below). **Returns `501` when the harness is disabled.** |

**Request body** (optional):

```json
{
  "cases": [
    {
      "taskGoal": "Open the Pricing page.",
      "observation": {
        "url": "https://example.com/",
        "title": "Acme — Home",
        "elements": [
          { "ref": 1, "role": "link", "name": "Home" },
          { "ref": 2, "role": "link", "name": "Pricing" }
        ]
      },
      "expectedRef": 2,
      "acceptableRefs": [2]
    }
  ]
}
```

- `cases` omitted / `null` / empty ⇒ the built-in **default fixture set**
  (`GroundingEvalFixtures.Default()`, a handful of benign navigation / search / cart cases).
- `acceptableRefs` is optional; when omitted, only `expectedRef` counts as correct.
- At most **200** cases per request; each case needs a non-empty `taskGoal` (≤ 4000 chars)
  and an `observation`. Violations return `400`.

**Response** — the aggregate report:

```json
{
  "malformed": 1,
  "nonElement": 0,
  "hallucinated": 1,
  "validButWrong": 1,
  "correct": 2,
  "total": 5,
  "groundingAccuracy": 0.4,
  "hallucinationRate": 0.2,
  "cases": [
    {
      "taskGoal": "Open the Pricing page.",
      "outcome": "Correct",
      "chosenRef": 2,
      "actionType": "Click",
      "expectedRef": 2,
      "acceptableRefs": [2],
      "rawOutput": "{\"action\":\"click\",\"ref\":2}",
      "parseError": null
    }
  ]
}
```

---

## 4. Outcome classification

For each case the harness builds a `ComputerUsePrompt` (reusing
`ComputerUseAgent.SystemPrompt`), calls `IComputerUseModel.DecideNextActionAsync`, parses the
reply with `ComputerUseActionParser.TryParse`, and classifies it by the chosen `ref`:

| Bucket | Meaning |
| --- | --- |
| **Malformed** | The output could not be parsed into a valid action. |
| **NonElement** | Parsed, but not a ref-targeting action (done / ask / scroll / navigate / wait / screenshot / key, or a coordinate-only click/type). |
| **Hallucinated** | A ref-targeting action whose `ref` is **not** in the observation's elements. |
| **ValidButWrong** | The `ref` exists in the observation but is not one of the case's acceptable refs. |
| **Correct** | The `ref` is one of the case's acceptable refs. |

`GroundingAccuracy = Correct / Total`, `HallucinationRate = Hallucinated / Total` (both `0`
for an empty run). A single case's model error is contained as a **Malformed** outcome (its
message recorded in `parseError`) so one bad case can't abort the batch; cancellation
propagates.

---

## 5. What is CI-verified

**CI-verified** (no container / no model load / no DB, via a scripted `IComputerUseModel`):

- `GroundingEvaluatorTests` — classification of a correct-ref, valid-but-wrong-ref,
  hallucinated-ref, non-element, and malformed case into the right buckets; the multi-ref
  `AcceptableRefs` case; `GroundingAccuracy` / `HallucinationRate` over a mixed batch; the
  disabled-refuses path; empty-batch (zero, not NaN); null-cases guard; a contained model
  exception; that the prompt reuses `ComputerUseAgent.SystemPrompt`; tolerant fenced-JSON
  parsing; and the `Classify` helper across every bucket.
- `GroundingEvalControllerTests` — `501` when disabled, the default-fixture path on an empty
  body, pass-through of supplied cases, `400` on too many cases / an empty goal, and the
  Admin-only route/attribute (also covered centrally by `ControllerAuthorizationTests`).

**Live-only** (needs the real stack): the actual `ComputerUseModel` inference via
`LmModelManager` — the harness's whole point is to score *that* model, so real numbers come
from a running deployment with the model loaded.
