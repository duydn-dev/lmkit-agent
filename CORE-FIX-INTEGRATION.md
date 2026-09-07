# CORE-FIX-INTEGRATION

Changes that belong in files owned by other agents. Everything below is **optional**:
the core fixes ship complete and behaviour-preserving without any of it. Applying a
snippet only turns on something that is deliberately off by default, or closes a
documented residual gap.

Source of these notes: the chat/agent-core fix pass covering
`Infrastructure/AI/AgentOrchestrator.cs`, `Infrastructure/AI/Research/DeepResearchService.cs`
and `Infrastructure/AI/AgentMemoryService.cs`.

---

## 1. `AgentMemory:RecallUnconfirmed` — optional, default OFF

### What it is

`AgentMemoryService.ExtractAndStoreFactsAsync` writes heuristically extracted facts with
`IsConfirmed = false` (regex extraction is fallible, so it must never label an inferred
fact as user-confirmed). `RecallMemoriesAsync` recalls only confirmed memories. The loop
therefore closes **through the human**: the fact appears in `GET /api/memory`, the user
clicks the check in `MemoryView.vue`, `POST /api/memory/{id}/confirm` flips
`IsConfirmed`, and the next recall picks it up. That flow already works end to end.

`AgentMemory:RecallUnconfirmed = true` makes recall *also* consider unconfirmed memories,
i.e. closes the extract → embed → upsert → recall loop with no human step.

**It is privacy-sensitive.** Leaving it unset preserves today's behaviour exactly. Memory
*scope* (`MemoryScopePolicy` owner/tenant visibility) and expiry are unaffected either
way — only the confirmation requirement is relaxed.

The option type is `LmKitOmniApi.Infrastructure.AI.AgentMemoryOptions`
(`SectionName = "AgentMemory"`), and the service takes it as an **optional** constructor
parameter (`IOptions<AgentMemoryOptions>? memoryOptions = null`). So the DI graph resolves
with or without the binding below — the binding is only needed to make the setting
configurable.

### `Program.cs`

Add next to the other option bindings (the `ChatReasoningOptions` line, currently ~line 255):

```csharp
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.AgentMemoryOptions>(builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.AgentMemoryOptions.SectionName));
```

### `appsettings.json`

Add a sibling of the existing `"ChatReasoning"` block (currently ~line 190). Shown at its
default, which is also the value to ship unless auto-recall of inferred facts is an
accepted product decision:

```json
  "AgentMemory": {
    "RecallUnconfirmed": false
  },
```

### Tests already covering both settings

`LmKitOmniApi.Tests/AgentMemoryRecallTests.cs`:

- `Recall_ExcludesUnconfirmedMemories_ByDefault`
- `Recall_IncludesUnconfirmedMemories_WhenRecallUnconfirmedIsEnabled`
- `RecallUnconfirmed_DoesNotWidenMemoryScope`
- `RecallUnconfirmed_StillHonoursExpiry`
- `ConfirmMemory_MakesAnExtractedFactRecallable_WithDefaultSettings`

---

## 2. Approved-action scope — one residual gap, optional to close

`AgentOrchestrator.ExecuteDirectActionAsync` no longer passes `options: null` into
`ExecuteActionCoreAsync`. It now recovers the requesting turn's scope with
`AgentOrchestrator.ResolveApprovedActionOptionsAsync`, walking
**approval → `ChatSessionId` → session → bound `CustomAgentId` → custom agent**, which is
the single source of `AllowedTools`, `KnowledgeDocumentIds` and `AllowWebSearch`.

**No change is required in `Application/Approvals/*`.** `ApproveTaskCommandHandler` already
passes `request.TaskId` as `approvalId`, which is all the recovery needs.

### The residual gap

Deleting a custom agent NULLs every session's binding to it — `ChatSession.CustomAgentId`
is configured `DeleteBehavior.SetNull` in `HermesDbContext` (~line 107). A pending approval
from such a session then resolves as unbound and executes unscoped (pre-fix behaviour).
Every other case is covered: agent still visible → its scope; agent present but no longer
visible to the caller (un-shared) → deny-all.

Pinned by `ApprovedActionScopeTests.ApprovedAction_FallsBackToUnscoped_WhenTheBoundAgentWasDeleted`
so it cannot change silently.

### If you want to close it (schema change — not applied here)

Snapshot the scope onto the approval row at creation time. Owner of
`Domain/Entities/TaskApproval.cs` + a migration:

```csharp
    /// <summary>
    /// JSON snapshot of the AgentRequestOptions narrowing fields (AllowedTools,
    /// KnowledgeDocumentIds, AllowWebSearch) in force on the turn that requested this
    /// approval. Null for approvals created before this column, and for unscoped turns.
    /// </summary>
    public string? RequestOptionsJson { get; set; }
```

The orchestrator writes it where it already builds the row
(`AgentOrchestrator.ExecuteActionWithResilienceAsync`, at the `new TaskApproval { … }`),
and `ResolveApprovedActionOptionsAsync` prefers it over the session walk. Both edits are
in orchestrator code and can be made without touching `Application/Approvals/*`.

### Optional: an explicit override on the interface

If a caller ever needs to pass live options instead of relying on recovery, add a
**trailing** optional parameter to `IAgentOrchestrator.ExecuteDirectActionAsync` (trailing,
so `ApproveTaskCommandHandler`'s existing positional call still compiles):

```csharp
    Task<string> ExecuteDirectActionAsync(
        Guid tenantId, Guid userId, string action, string query,
        Guid? approvalId = null, CancellationToken ct = default,
        AgentRequestOptions? options = null);
```

Not added, because nothing needs it today and recovery already covers the real callers.

---

## 3. `[THINKING]` marker newline fix — nothing to integrate

`"…\\n"` in non-verbatim C# literals put the two characters `\` + `n` on the wire instead
of a newline, and both strippers are line-anchored
(`StreamChatCommandHandler.StripProtocolMarkers`'s `\[THINKING\]:[^\n\r]+[\n\r]*`, and
`LmKitOmniClient/src/composables/useChatStream.ts`'s `parseStoredAssistantContent`), so
`[^\n\r]+` ran through the answer itself.

All 42 occurrences were fixed at the emitters, so **the wire format is corrected and
neither stripper needs to change** — the client fix is the server fix. No edit is needed
in `StreamChatCommandHandler.cs` or in `useChatStream.ts`.

Pinned by `LmKitOmniApi.Tests/ProtocolMarkerStreamTests.cs`, which drives a real emitter
and feeds its output through the shipping server stripper.
