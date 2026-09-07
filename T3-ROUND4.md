# T3 — round 4: resuming an agent run after a human approval

Branch `round4/t3-approval-resume`, based on master `c999f12`. Closes known-issues **#7**.

---

## (a) What a resumed run does, end to end — and when it does not resume

### The redesign in one sentence

The orchestrator's ReAct pass **carries no session history by design** — its entire input is a
query string plus a context string (`AgentOrchestrator.ExecuteNativeReActAsync`). So the pass has
no hidden in-process state to recover, and everything it saw is already persisted: `agent_runs.Goal`,
the ordered `agent_run_steps` (whose last row is the approved call **and its real output**, written
by `AgentRunApprovalReconciler`), and the scope snapshot on `task_approvals.RequestOptionsJson`.
Rebuilding that input from rows and running the pass again is not an approximation of the dead
`await foreach` — it is the same input **plus** the observation the loop was waiting for.

That is why the continuation needed exactly **one** new piece of state: a marker saying which runs
are owed a pass and whether somebody is driving one.

### The flow

1. A run trips a gate → `AwaitingApproval`, step 1 records the attempt with
   `[HITL_APPROVAL_REQUIRED:{id}]` as its observation. **Unchanged.**
2. A human approves. `ApproveTaskCommandHandler` executes the tool (unchanged), then
   `AgentRunApprovalReconciler` records the approved call as a real step with the real output and
   appends it to `AgentRun.Result` and to the hidden session transcript. **Unchanged.**
3. **New:** instead of ending the run, the reconciler sets `Status = Running`,
   `ResumeState = Pending`, `CompletedAtUtc = null` — in the same `SaveChanges` — and the handler
   nudges the in-process worker.
4. `AgentRunResumeWorker` (poll + nudge) claims the run, and `AgentRunResumeService`:
   - re-checks the user is still active in the tenant (an approval bought one decision, not
     standing authority) — otherwise the run is `Failed` with a user-safe reason;
   - reads goal + every step from the database and composes the continuation query
     (`AgentRunResumePrompt`): goal, then the prior tool results inside an explicit
     *untrusted-data* fence, then "these already ran, do not call them again, finish the goal";
   - resolves the execution scope with the **same** `AgentOrchestrator.ResolveApprovedActionOptionsAsync`
     intersection the approved execution itself ran under, so a continuation can never be wider
     than the turn that asked for the approval;
   - drives `IAgentOrchestrator.StreamProcessQueryAsync` with a step sink — the full pipeline:
     input guardrail, memory recall, chat lease, ReAct, synthesis, streaming output guardrail;
   - persists the new steps (ordinals continuing from the max) and lands the run.
5. Outcomes of a continuation pass:
   - **Completed** — synthesized answer appended to `Result`, `CompletedAtUtc` set.
   - **AwaitingApproval** — it hit a SECOND gate. The new marker step is persisted, which is how
     both the reconciler (session + `AwaitingApproval`) and the agent-run page (last marker step)
     find it. Approving again buys another pass, budget permitting. The lifecycle is a loop.
   - **Failed** — the pass threw; user-safe error only.
   - **Re-queued** — the process is shutting down. Steps that really ran are kept, `ResumeState`
     goes back to `Pending`, and the pass replays them next time. Budget was already spent by the
     claim, so this cannot spin.

### It keeps the truthful stop in exactly four cases

`CompletedAfterApproval` with `CompletedAtUtc` set, i.e. byte-identical to the shipped behaviour,
and in the last two the **reason is written into `Result`** rather than left to be inferred:

| Case | Why |
|---|---|
| The decision was reject / tool-failure / expiry | Nothing to continue *from*. A refusal is the end of the plan, not an observation. |
| The host never registered a continuation worker | `ApproveTaskCommandHandler` takes `AgentRunResumeQueue?` as an **optional** dependency defaulting to `null`. A configuration flag could not decide this — it reads the same whether or not `AddAgentRunResume` was called — and queuing work nobody will drive would reinvent "parked forever". |
| `AgentRunResume:Enabled = false` | And anything queued while it was on is **drained** to the same terminal state on the worker's next start, so flipping it off never strands a run. |
| The run has spent its resume or step budget | Stated in the result: *"…đã đạt giới hạn, nên dừng tại đây."* |

### What the client sees (no client change, and none needed)

A resumed run reports `Running` — a status `agentRunStatus.ts` already renders ("Đang chạy",
non-terminal). Adding a `Resuming` word would have rendered as an unknown grey pill on a client I
do not own; the continuation's own bookkeeping lives on `AgentRun.ResumeState` instead, so nothing
in the API's status vocabulary changed. The agent-run page's `refreshGatedRun` sees `status !==
'AwaitingApproval'`, closes the gate buttons and shows "Đang chạy" — correct. A second gate returns
the run to `AwaitingApproval` and `adoptGateFrom` re-adopts it from the persisted marker step, so
the two-gate flow works with zero client change. **Chat is untouched**: a chat approval has no
`AgentRun`, the reconciler still returns `NoParkedRun`, nothing is queued (test:
`Approve_ForAnOrdinaryChatApproval_QueuesNothingAndWritesNoRunRows`).

---

## (b) Durability, idempotency, bounds — and the test that holds each

### Durability

Nothing is carried in memory. Every test approves through the real handler and then drives the
continuation from a **fresh `HermesDbContext`**, exactly as a worker on another replica would.
New state is three columns on `agent_runs`: `ResumeState` (null | Pending | Claimed), `ResumeCount`,
`ResumeLeaseUntilUtc`. The in-process `AgentRunResumeQueue.Notify()` is a latency optimisation only —
the queue *is* the column, so a missed nudge costs seconds, never the continuation.

### Idempotency and re-entrancy

- `AgentRunApprovalReconciler.ResolveAsync` still filters `Status == AwaitingApproval`. That is now
  what stops a double-clicked approve from buying **two** continuation passes, and it holds for two
  replicas racing just as well, because leaving `AwaitingApproval` is a single UPDATE.
  → `ApprovingTwice_QueuesExactlyOneContinuation_AndResumesOnce`
- The claim is a **compare-and-swap on (`ResumeState`, `ResumeCount`)** that also increments the
  counter, so exactly one worker wins. → `TwoWorkersThatBothSawTheSameQueuedRun_ProduceExactlyOneClaim`
- `ResumeCount` is then the **fencing token**: the release at the end of a pass is conditioned on it,
  so a worker whose lease lapsed mid-pass discovers it lost ownership and **discards** its work
  instead of writing a second copy of the steps. Integers were chosen over the lease timestamp
  precisely because they survive a database round-trip exactly.
- `CloseWithoutResumingAsync` claims the close the same way (`WHERE ResumeState IS NOT NULL`).

### Bounds

| Bound | Default | Enforced where | Test |
|---|---|---|---|
| Resumes per run | `MaxResumesPerRun = 3` | at enqueue (reconciler) **and** in the claim predicate | `ResumeBudget_Exhausted_ClosesTheRunTruthfullyAndSaysWhy`, `AQueuedRunThatIsAlreadyOverBudget_IsClosedByTheWorker_NotReselectedForever` |
| Total steps per run | `MaxTotalSteps = 24` | at enqueue and before each claim | `StepCap_Reached_ClosesTheRunTruthfullyInsteadOfResuming` |
| Concurrent passes | 1 per replica (the worker drives runs sequentially) | worker loop | — |
| Runs per poll | `MaxRunsPerPass = 4` | `ResumePendingAsync` | — |
| Claim lease | `LeaseSeconds = 900` | retake only after it lapses | `AClaimWhoseLeaseLapsed_IsRetakenByTheNextWorker`, `AClaimStillWithinItsLease_IsLeftAlone` |
| Prompt size | 2 000 chars/observation, 8 000 total | `AgentRunResumePrompt` | 4 tests in `AgentRunResumePromptTests` |

Budget is spent on the **claim**, not on success, so a crash loop or a shutdown loop terminates.
A run that hits any bound is **closed truthfully**, never left queued — that is what makes the
queue always drain.

### The model lease

`StreamProcessQueryAsync` takes the single chat permit with `await using` **inside** its iterator, so
it is released only if the consumer disposes the enumerator. The worker's `await foreach` inside
`try`/`catch(OperationCanceledException)` does exactly that, and finalisation always runs with
`CancellationToken.None` so a shutdown never loses work that really happened.
→ `Cancellation_MidResume_ReleasesTheModelLease_AndRequeuesTheRun` holds a real single-permit
`SemaphoreSlim` the same way the orchestrator does, asserts `CurrentCount == 0` while the pass runs
and `== 1` after cancellation, and asserts the run came back as `Running`/`Pending` with its
partial steps kept.

---

## (c) Migration

**`20260907065953_AgentRunResume`** — `agent_runs` gains `ResumeState varchar(16) NULL`,
`ResumeLeaseUntilUtc timestamptz NULL`, `ResumeCount integer NOT NULL`, plus
`IX_agent_runs_ResumeState_ResumeLeaseUntilUtc`.

`ResumeCount` follows the `TaskApprovalExpiry` precedent — added **nullable → backfilled → tightened**
rather than the scaffolder's `nullable: false, defaultValue: 0`. That form would have worked, but it
leaves a permanent `DEFAULT 0` the EF model knows nothing about, so schema and snapshot would
disagree forever. Verified PostgreSQL output via `dotnet ef migrations script`:

```sql
ALTER TABLE agent_runs ADD "ResumeState" character varying(16);
ALTER TABLE agent_runs ADD "ResumeLeaseUntilUtc" timestamp with time zone;
ALTER TABLE agent_runs ADD "ResumeCount" integer;
UPDATE agent_runs SET "ResumeCount" = 0 WHERE "ResumeCount" IS NULL;
ALTER TABLE agent_runs ALTER COLUMN "ResumeCount" SET NOT NULL;
CREATE INDEX "IX_agent_runs_ResumeState_ResumeLeaseUntilUtc" ON agent_runs ("ResumeState", "ResumeLeaseUntilUtc");
```

No column default is left behind. Runs already sitting at `AwaitingApproval` are deliberately **not**
queued by the migration — nobody has approved them yet — and take the new path with a full budget
when their approval is answered.

`dotnet ef migrations has-pending-model-changes` → **"No changes have been made to the model since
the last migration."**

---

## (d) What was proved, with which test

**Suite:** `dotnet test LmKitOmniApi.Tests` → **1473 passed / 0 failed / 11 skipped**, twice, the
second time with `-- xUnit.MaxParallelThreads=32`. Master baseline was 1446 / 0 / 10; I added 27
tests and 1 opt-in skip. `dotnet build` clean — the only warnings are pre-existing (`NU1903` SSH.NET
in IntegrationTests, `CS0618` LicenseManager in `Program.cs`).

### Before-fix observation

I neutralised the fix by making `AgentRunApprovalReconciler.ResumeStopReason` return `string.Empty`
unconditionally — i.e. exactly the shipped "never resume" behaviour — rebuilt and ran the two new
files: **9 failed, 18 passed.** The failures are precisely the resume claims:

```
Approve_HandsTheRunBackToTheReActLoop_InsteadOfEndingIt
Resume_ReplaysTheApprovedObservation_AndProducesFurtherSteps
Resume_ThatHitsASecondGate_ParksTheRunAgain_AndCanBeApprovedIntoAnotherPass
ApprovingTwice_QueuesExactlyOneContinuation_AndResumesOnce
TwoWorkersThatBothSawTheSameQueuedRun_ProduceExactlyOneClaim
Cancellation_MidResume_ReleasesTheModelLease_AndRequeuesTheRun
Resume_ForAUserWhoIsNoLongerActive_FailsTheRunInsteadOfPlanning
ResumeBudget_Exhausted_ClosesTheRunTruthfullyAndSaysWhy      (the "says why" note is new)
StepCap_Reached_ClosesTheRunTruthfullyInsteadOfResuming      (ditto)
```

The 18 that passed both before and after are the ones that pin behaviour I deliberately **preserved**:
reject / expiry / tool-failure still terminal, chat untouched, no-worker host unchanged, prompt
composition, DI resolution.

### The named coverage you asked for

| Requirement | Test |
|---|---|
| an approved gated call resumes and produces a further step | `Resume_ReplaysTheApprovedObservation_AndProducesFurtherSteps` — asserts step 3 exists, the query contained the approved observation and the goal, and did **not** contain the `[HITL_APPROVAL_REQUIRED:` marker |
| a resumed run that hits a second gate parks correctly | `Resume_ThatHitsASecondGate_ParksTheRunAgain_AndCanBeApprovedIntoAnotherPass` — and then approves it into a second pass |
| resolving the same approval twice does not double-step | `ApprovingTwice_…` (+ `TwoWorkersThatBothSaw…` for the replica version) |
| a rejected call still reaches the truthful terminal state | `Reject_StillReachesTheTruthfulTerminalState_AndQueuesNothing`, `Expiry_…`, `Approve_WhenTheToolThrows_…` |
| the step cap holds | `StepCap_Reached_…`, `AQueuedRunThatIsAlreadyOverBudget_…` |
| cancellation/shutdown mid-resume releases the lease | `Cancellation_MidResume_ReleasesTheModelLease_AndRequeuesTheRun` |

Also covered: crash recovery via lapsed lease, drain-when-disabled, authority re-check at
continuation time, and `ApproveHandler_ResolvesFromDiWithoutAResumeQueueRegistered` (proves
Microsoft DI fills the optional constructor parameter — if it did not, a host without the worker
would 500 on every approval).

### Live proof (opt-in, ran green on this machine)

`LmKitOmniApi.Tests/AgentRunResumeLiveTests.cs`, gated on `LMKIT_LIVE_SEAM_TEST=1` + a reachable
GGUF, skipping cleanly otherwise (it skips in the suite above). It builds the real continuation
prompt from a staged step history and runs it through a real `LMKit.Agents.AgentExecutor` with
`PlanningStrategy.ReAct` and one registered tool. **Passed against
`Llama-3.2-1B-Instruct-Q4_K_M.gguf`**: the agent answered from the replayed approved observation
(an invented SKU `ZORBIX-4417` priced at 42 USD, so the answer can only come from the replay), which
is the premise the whole design rests on.

```
LMKIT_LIVE_SEAM_TEST=1 \
LMKIT_LIVE_SEAM_MODEL="C:\Users\DuyDN\AppData\Local\models\lm-kit\llama-3.2-1b-instruct.gguf\Llama-3.2-1B-Instruct-Q4_K_M.gguf" \
dotnet test LmKitOmniApi.Tests --filter "FullyQualifiedName~AgentRunResumeLiveTests"
→ Passed! - Failed: 0, Passed: 1, Skipped: 0  (26 s)
```

---

## (e) For you to apply

**`Program.cs`** — one line, next to the existing `AddApprovalExpiry` call (line 407):

```csharp
LmKitOmniApi.Application.AgentRuns.AgentRunResumeServiceCollectionExtensions.AddAgentRunResume(builder.Services, builder.Configuration);
```

Without it nothing changes: the handler resolves a null queue and every run keeps the old truthful
stop. That is by design, not a degradation.

**`appsettings.json`** — optional; these are the exact code defaults, so omitting the block behaves
identically. Suggested placement is right after the `"ApprovalExpiry"` block (line 90-96):

```json
  "AgentRunResume": {
    "Enabled": true,
    "MaxResumesPerRun": 3,
    "MaxTotalSteps": 24,
    "PollIntervalSeconds": 5,
    "LeaseSeconds": 900,
    "MaxRunsPerPass": 4,
    "MaxObservationChars": 2000,
    "MaxProgressChars": 8000
  },
```

Nothing needs adding to the integration-test host: it never calls `AddAgentRunResume`, so no
background worker starts there and the suite is unaffected.

---

## (f) known-issues #7

**Delete it.** The entry says the run "is **not** a resume … That is a redesign, not a fix." The
redesign is done, the run resumes, and both the mechanism and the conditions under which it *doesn't*
are documented in code (`AgentRunApprovalReconciler`, `AgentRunResumeService`, `AgentRunStatuses`).
Add **#7** to the "Fixed so far" line as `#7 an approved gate not resuming the ReAct loop`.

If you would rather keep an entry, the honest narrowed version is:

> ## 7. A resumed run is bounded, and stops truthfully when it runs out of budget
>
> Not a defect; recorded so the bound is not mistaken for the old "never resumes" behaviour. An
> approved gated call now hands the run back to the ReAct loop with the approved observation replayed
> as prior progress (`LmKitOmniApi/Application/AgentRuns/AgentRunResumeService.cs`). That continuation
> is capped by `AgentRunResume:MaxResumesPerRun` (3) and `MaxTotalSteps` (24), because a pass runs
> outside any HTTP request and takes the single chat inference lease. A run that reaches either cap —
> or one approved in a host that did not register the worker, or with `AgentRunResume:Enabled=false` —
> ends at `CompletedAfterApproval` with the reason written into its result, which is the same truthful
> terminal state the run always reached.

---

## (g) What I could not do, and two findings you should see

### 1. BLOCKING for live use — `AgentExecutor.MaximumCompletionTokens` throws before `Execute`

`LmKitOmniApi/Infrastructure/AI/AgentOrchestrator.cs:610-611`

```csharp
using var executor = new AgentExecutor();
executor.MaximumCompletionTokens = DefaultMaximumCompletionTokens;   // ← throws
```

On **LM-Kit.NET 2026.9.0** that setter throws
`InvalidOperationException: "Conversation has not been initialized. Call ExecuteAsync first or
provide a conversation in the constructor."` — the property delegates to a conversation that only
`Execute`/`ExecuteAsync` creates. I reproduced it in isolation with the orchestrator's exact
sequence (fresh `AgentExecutor`, real model loaded, agent built with `PlanningStrategy.ReAct` and a
registered tool). This is on the path of **every chat turn and every agent run**
(`StreamProcessQueryAsync → ExecuteNativeReActAsync`), it is not caught anywhere below the
controller, and it is invisible to all 1446 tests because they fake the model boundary. My live
proof only passes because it omits that setter.

**I did not fix it.** It is unrelated to #7, it is a shared hot path, and both available fixes carry
real design content that is yours to choose:

- **(a)** delete the line — the ReAct pass then uses LM-Kit's default completion cap, losing an
  intended 2 048-token bound (the synthesis pass keeps its own, which is set on a
  `MultiTurnConversation` and is unaffected);
- **(b)** `new AgentExecutor(conversation)` — the only other public constructor takes a
  `MultiTurnConversation`, which lets the cap be set up front but puts the ReAct pass into the same
  territory as known-issues #4 (constructing on a non-empty history adopts `Messages[0]` as the
  system prompt).

There is no builder hook: I enumerated `AgentBuilder` and it has no completion-token method.
Until this is resolved, a live resume — like every live ReAct pass — will fail; the continuation
machinery around it is complete and proven, and my worker records that failure as `Failed` with a
user-safe error rather than looping.

### 2. The shared marker regex leaves `[THINKING]:` lines in stored results

`AgentRunMarkers.StripMarkers` (extracted verbatim from `StreamAgentRunCommandHandler`, where it
already lived) does not strip a `[THINKING]: …\n` line when more text follows on the next line:
`[^\n\r]*?` cannot cross the newline, so neither the `\]` nor the `(?=\[)` nor the `$` branch can
close the match. Verified against three realistic streams. Pre-existing, so every agent-run result
already carries these lines; I left it alone rather than change first-pass behaviour in passing, and
a resumed run is therefore shaped exactly like a first-pass one. One extra alternative
(`|[\n\r]+` after `\][\n\r]*`) fixes it if you want it fixed.

### 3. Smaller things I chose not to do

- **No client change.** A resumed run reports `Running` and the agent-run page does not poll, so the
  user must reload to see the finished answer. That is `LmKitOmniClient/**` work and not mine this
  round; the API needs nothing for it.
- **Cross-replica nudge.** `AgentRunResumeQueue.Notify()` is in-process. A continuation queued on
  replica A is picked up by replica B on its next poll (≤ 5 s by default), never lost. A push
  (Redis pub/sub, `NOTIFY`) would only remove that latency; correctness does not depend on it, so I
  did not add infrastructure I cannot test here.
- **Memory extraction sees the continuation query.** `StreamProcessQueryAsync` ends with
  `ExtractAndStoreFactsAsync(..., query, ...)`, and on a resume that query contains the fenced
  replay of prior tool output. Facts extracted from tool output already reach memory through the
  answer, so this widens nothing new — but it is worth knowing that a resumed pass's "user message"
  is larger and tool-derived.
- **No Postgres run.** No PostgreSQL instance is available here; the migration is verified by
  generated script + `has-pending-model-changes`, and the schema itself is exercised on SQLite by the
  suite via `EnsureCreated`.

---

## Files changed

**New** — `Application/AgentRuns/`: `AgentRunResumeStates.cs`, `AgentRunResumeOptions.cs`,
`AgentRunResumeQueue.cs`, `AgentRunResumePrompt.cs`, `AgentRunResumeService.cs`,
`AgentRunResumeWorker.cs` (worker + history seam + DI extension), `AgentRunMarkers.cs`;
`Application/Approvals/AgentRunResolution.cs`; `Migrations/20260907065953_AgentRunResume*.cs`;
tests `AgentRunResumeTests.cs`, `AgentRunResumePromptTests.cs`, `AgentRunResumeLiveTests.cs`.

**Modified** — `Domain/Entities/AgentRun.cs` (3 columns), `Infrastructure/Data/HermesDbContext.cs`
(1 index), `Application/AgentRuns/AgentRunStatuses.cs` (docs),
`Application/AgentRuns/Handlers/StreamAgentRunCommandHandler.cs` (uses the shared marker helper),
`Application/Approvals/AgentRunApprovalReconciler.cs` (the resume decision),
`Application/Approvals/Handlers/ApproveTaskCommandHandler.cs` (optional queue + nudge),
`LmKitOmniApi.Tests/AgentRunApprovalLifecycleTests.cs` (docs + explicit `resumeQueue: null`),
`Migrations/HermesDbContextModelSnapshot.cs` (scaffolded).

**`Infrastructure/AI/AgentOrchestrator.cs` was NOT modified** — deliberately, so T1's pending
call-site change around `ChatConversationFactory.Create` / `RegisterSafeDefaults` merges without a
conflict. The continuation drives the orchestrator through its existing `IAgentOrchestrator`
contract; no interface change either, so no other agent's fakes are affected.
