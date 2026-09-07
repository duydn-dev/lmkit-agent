# Agent-run / approval lifecycle fix — integration notes

Scope of the change that produced this file: **T7** (an agent run that hits an
approval gate was stuck in `AwaitingApproval` forever) and **T15** (tests for
`StreamingGuardrailGate` and `StreamChatCommandHandler`).

Everything below is either (a) a decision another owner should know about, or
(b) work that belongs to a file this change did not own and therefore did not
touch.

---

## 1. What changed (owned files only)

| File | Change |
| --- | --- |
| `LmKitOmniApi/Application/AgentRuns/AgentRunStatuses.cs` | **New.** The full `AgentRun.Status` vocabulary as constants, with which values are terminal. |
| `LmKitOmniApi/Application/Approvals/AgentRunApprovalReconciler.cs` | **New.** Closes the run lifecycle when a human resolves the gating `TaskApproval`. Static — no DI registration needed. |
| `LmKitOmniApi/Application/Approvals/Handlers/ApproveTaskCommandHandler.cs` | Calls the reconciler after a successful execution and after a failed one. Reconciliation failures are logged, never converted into a different HTTP outcome. |
| `LmKitOmniApi/Application/Approvals/Handlers/RejectTaskCommandHandler.cs` | Reads the approval before the claim so a rejection can also close the run; takes `TaskApprovalPayloadProtector` + `ILogger` (both already registered). |
| `LmKitOmniApi/Application/AgentRuns/Handlers/StreamAgentRunCommandHandler.cs` | Uses `AgentRunStatuses` instead of bare literals; comment explains that `AwaitingApproval` is now exited by the reconciler. |

### The lifecycle option chosen

**Honest terminal state + result on the run.** An approved gated call is
recorded as a real `AgentRunStep`, its output is appended to `AgentRun.Result`
*and* to the run's hidden `IsAgentRun` session as an assistant `ChatMessage`,
and the run moves to a terminal status with `CompletedAtUtc` set.

**This is explicitly NOT a resume.** Re-entering the ReAct loop with the approved
observation fed back as a tool result would need a durable continuation of the
streaming loop — the orchestrator's ReAct pass is an in-process `await foreach`
whose state dies with the request — and that is a redesign, not a fix. The code
says so in `AgentRunApprovalReconciler`'s class remarks and in
`AgentRunStatuses.CompletedAfterApproval`. A run that ends after one approved
tool call is a smaller answer than a resumed run would give, but it is a truthful
one, and it ends.

### New status values

| Status | Terminal? | Meaning |
| --- | --- | --- |
| `CompletedAfterApproval` | yes | Human approved; the gated tool ran; the ReAct loop was **not** resumed. |
| `Rejected` | yes | Human rejected the gated tool; nothing executed. |

`Failed` is reused when the approved tool throws. The existing `Running`,
`Completed`, `Failed`, `AwaitingApproval` values are unchanged.

---

## 2. Work for other owners

### 2.1 `LmKitOmniClient/src/views/agents/AgentRunsView.vue` — status labels (cosmetic)

`statusMeta()` has a `default` branch that renders the raw status string in a
grey pill, so the two new values degrade gracefully — but they show untranslated.
Add:

```ts
case 'CompletedAfterApproval':
  return { label: 'Hoàn tất sau phê duyệt', classes: 'bg-emerald-50 text-emerald-900 border-emerald-200' };
case 'Rejected':
  return { label: 'Đã từ chối', classes: 'bg-red-50 text-red-800 border-red-200' };
```

The type comment on line ~381 (`'' | 'Running' | 'Completed' | 'AwaitingApproval' | 'Failed'`)
should gain both values too.

### 2.2 `LmKitOmniApi/Domain/Entities/AgentRun.cs` — doc comment is now stale

Line 29 still reads `"Running" | "Completed" | "Failed" | "AwaitingApproval"`.
It should point at the constants instead:

```csharp
/// <summary>See <see cref="Application.AgentRuns.AgentRunStatuses"/> for the full vocabulary.</summary>
```

Not changed here because `Domain/**` was outside this change's ownership.

### 2.3 The agent-run UI has no approve button

`AgentRunsView.vue` captures the approval id from the `[HITL_APPROVAL_REQUIRED:]`
marker into `approvalTaskId` and then does nothing with it — the user has to find
the approval on the Approvals page. Now that resolving the approval actually
closes the run, surfacing approve/reject inline (reusing `useHitlActions`) and
re-polling `GET /api/agent-runs/{id}` afterwards would complete the loop.

### 2.4 Remaining hole: an approval nobody ever answers

There is **no expiry mechanism anywhere** for `TaskApproval` (no `ExpiresAtUtc`
column, no sweeper). A run whose approval is never approved or rejected still
sits at `AwaitingApproval` forever. Closing that needs, in one change:

1. an `ExpiresAtUtc` (or a fixed age cut-off) on `TaskApproval` + a migration;
2. a hosted `ApprovalExpiryWorker` that flips stale `Pending` rows to `Expired`
   and calls `AgentRunApprovalReconciler` with a new
   `RecordExpiryAsync` (a three-line addition — the private `ResolveAsync` core
   already takes the terminal status as a parameter);
3. registration in `Program.cs` + an `appsettings.json` toggle.

All three files were out of scope here (`Program.cs` / `appsettings.json` are
explicitly off-limits, `Domain/**` and `Infrastructure/Workers/**` are not owned).

### 2.5 Chat approvals resolved from the Approvals page lose their result

For an ordinary chat session the reconciler intentionally no-ops: chat drives its
own continuation client-side (`useHitlActions` in
`LmKitOmniClient/src/composables/useChatStream.ts` pushes the approved result back
as the next user turn). That works from the chat card — but if the user instead
approves the same task from **`ApprovalsView.vue`**, nothing is written to the
conversation and the tool output exists only in that page's HTTP response. Fixing
it means either persisting an assistant turn for chat approvals too (a chat-surface
behaviour change, and `StreamChatCommandHandler` is owned by another agent right
now) or having `ApprovalsView` navigate into the session and continue the turn.
Flagged, not fixed.

---

## 3. `StreamingGuardrailGate` findings (T15 part 1)

### 3.1 Nothing streams until the answer passes 512 characters — NOT fixed

`Infrastructure/AI/StreamingGuardrailGate.cs:123`:

```csharp
var safeLength = Math.Min(view.Length - HoldbackChars, OutputGuardrailFilter.MaxOutputLength);
if (safeLength <= _emitted.Length) return string.Empty;
```

With `HoldbackChars = 512`, `safeLength` is negative for any answer shorter than
512 characters, so **nothing is ever released mid-stream** and the whole answer
arrives in the orchestrator's end-of-stream tail — despite
`AgentOrchestrator.cs:353` advertising "TRUE token streaming". The stride makes it
slightly worse: emission is only *attempted* once 32 new characters have arrived,
so a 520-character answer delivered in 32-character chunks also streams nothing
(last attempt happens at 512).

Both behaviours are pinned by tests in
`LmKitOmniApi.Tests/StreamingGuardrailGateTests.cs`
(`AnswerShorterThanTheHoldbackWindow_StreamsNothingAtAll`,
`AnswerJustPastTheHoldbackWindow_StartsStreaming`,
`EmitStride_DefersReleaseUntilEnoughNewTextArrives`).

**Why it was not fixed:** the holdback is what makes the redaction correct, and
shrinking it re-opens the exact leak the gate exists to prevent. Concretely, the
email pattern's local part (`[A-Za-z0-9._%+-]+`) is unbounded, so a match can
begin arbitrarily far behind the live edge; with a shorter window the head of an
address streams out before the `@` proves it *was* an address, and the full pass
then replaces the whole span with `[EMAIL REDACTED]` — at which point the emitted
text is no longer a prefix of the final text, `AgentOrchestrator.cs:499` takes the
divergence branch, and the already-streamed fragment cannot be recalled. The same
argument applies to the credential rule (`KEYWORD \s* [:=]? \s* \S+` can complete
arbitrarily late). There is no holdback smaller than 512 that is provably safe for
these patterns, so the trade-off is real and the correct move is to make it
visible rather than to trade safety for latency silently.

**If a product decision says short answers must stream**, the honest way is to
change the *patterns*, not the window: bound the email local part (e.g. `{1,64}`,
which RFC 5321 allows) and the credential value gap, then set `HoldbackChars` to
the resulting worst-case match length. That is a redaction-semantics change and
needs its own review.

### 3.2 Detector observation: `\b` anchors miss a secret glued to a word

`PromptGuardService.LeakagePatterns` and `OutputGuardrailFilter`'s SSN/email
patterns are `\b`-anchored, so `123-45-6789The` and `bob@example.comThe` are
matched by neither the detector nor the redactor — in the streaming path *and* in
the non-streaming one. This is a pre-existing `OutputGuardrailFilter` /
`PromptGuardService` property, not a gate bug, and real model output separates
tokens; it is recorded here only so nobody rediscovers it as a "streaming leak".
No test asserts it, deliberately — pinning a weakness would make it harder to fix.

---

## 3a. `StreamChatCommandHandler` observation: the injected summary is merged into the next user turn

`StreamChatCommandHandler.cs:144-149` replays the trimmed history into
`ChatHistory`, mapping the rolling-summary row (`role == "system"`) to
`AuthorRole.User` "as user context". LM-Kit's `ChatHistory` **merges consecutive
same-role turns with a newline**, so the summary does not arrive as its own block:
the model receives one user turn containing `"<summary>\n<first real user turn>"`.

That is probably benign — but it is invisible from the handler's `AddMessage`
loop, and it means the summary cannot be distinguished from the user's own words
by the model. If that matters, the summary needs a delimiter of its own (e.g.
wrapping it in a labelled block) rather than a bare `AddMessage`. Pinned by
`StreamChatCommandHandlerTests.History_IsLoadedChronologically_TrimmedByTheTokenService_AndItsSummaryIsInjectedAndStored`
so a future change to it is deliberate. `StreamChatCommandHandler` was owned by
another agent during this change, so nothing was modified there.

---

## 4. Test entry points added

| File | Covers |
| --- | --- |
| `LmKitOmniApi.Tests/AgentRunApprovalLifecycleTests.cs` | T7: approve/reject/failure transitions, step recording, result persistence, resolve-once, chat approvals untouched, cross-user scoping. |
| `LmKitOmniApi.Tests/StreamingGuardrailGateTests.cs` | T15.1: split-secret cases (credential/SSN/email, every split point, every chunk size, every offset), byte-identity with the non-streaming pass, truncation cap, the holdback findings above. |
| `LmKitOmniApi.Tests/StreamChatCommandHandlerTests.cs` | T15.2: history trim + summary injection, 50-message cap, cache preference, ephemeral branch, regenerate / replace-last-exchange, cache invalidation, marker round-trip on the cancellation path. |

`StreamChatCommandHandlerTests` fakes the model boundary by setting
`LmModelManager`'s private `_chatModel` field to an uninitialized `LM`
(`RuntimeHelpers.GetUninitializedObject`), which is enough for the `ChatHistory`
the handler builds. If `LmModelManager.GetChatModelAsync` is ever refactored
behind an interface, those tests should switch to it and drop the reflection.
