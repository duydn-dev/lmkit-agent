# Known issues — diagnosed, not fixed

The single place this repo tracks defects that have been **located in code** but deliberately
left unfixed. Every entry names the file and the reason it is still open. Fix an entry →
delete it from this file in the same change.

Not a backlog of ideas. Something only belongs here once someone has read the code and can
point at the line.

---

## 0. HIGH — the embeddable widget can never actually be framed

`LmKitOmniClient/nginx.conf:39-43`

```nginx
# comment claims: "Subrequests inherit the parent's query string, so ?key= reaches the API"
location = /internal/widget-frame-policy {
    internal;
    proxy_pass http://api:5000/api/widget/frame-policy$is_args$args;
```

The comment is false: an `auth_request` subrequest does **not** inherit the parent request's
args, so `$is_args$args` expands to nothing. Measured against a running full stack:

- the subrequest reaches the API as `GET /api/widget/frame-policy` with **no query string**, and
  the API correctly answers `204` + `X-Widget-Frame-Ancestors: 'none'`;
- the same endpoint called directly *with* the key returns the tenant's real allowlist.

So the API is right and nginx drops the key. The `map` then fails closed and **every** embedded
widget document is served with `frame-ancestors 'none'` — unframeable by any customer site. The
fail-closed default is well designed; it is just always firing.

Caught by `LmKitOmniClient/e2e-fullstack/application.fullstack.spec.ts:116`.

**Fix:** capture the arg in the parent location, where variables *are* shared with the
subrequest, and correct the comment:

```nginx
location = /widget/chat {
    set $widget_key $arg_key;
    auth_request /internal/widget-frame-policy;
    …
}
location = /internal/widget-frame-policy {
    internal;
    proxy_pass http://api:5000/api/widget/frame-policy?key=$widget_key;
    …
}
```

---

## 1. `ToolSandboxService` pins its allowed roots at type-init time

`LmKitOmniApi/Infrastructure/AI/Security/ToolSandboxService.cs:27-34`

```csharp
private static readonly string[] DefaultAllowedPaths = new[]
{
    Path.Combine(Directory.GetCurrentDirectory(), "Uploads"),
    …
};
```

A `static readonly` initializer captures `Directory.GetCurrentDirectory()` **once**, the first
time the type is touched. Any later change to the process working directory silently
invalidates every sandbox root: file paths that should resolve inside `Uploads/` no longer do,
and the sandbox rejects legitimate paths (or accepts nothing at all).

Nothing in production changes the cwd today, so this does not currently misbehave in a running
server — but it is a latent trap, and it is why a single test that calls
`Directory.SetCurrentDirectory` can knock over unrelated test classes.

**Fix:** resolve the roots lazily per call, or anchor them to `AppContext.BaseDirectory`
instead of the mutable cwd.

---

## 2. An approval whose custom agent was deleted executes unscoped

`LmKitOmniApi/Infrastructure/AI/AgentOrchestrator.cs` (`ResolveApprovedActionOptionsAsync`)

Approved actions recover the requesting turn's tool scope by walking
approval → `ChatSessionId` → session → bound `CustomAgentId` → custom agent. That agent is the
only source of `AllowedTools`, `KnowledgeDocumentIds` and `AllowWebSearch`.

Deleting a custom agent NULLs every session's binding to it — `ChatSession.CustomAgentId` is
configured `DeleteBehavior.SetNull` in `HermesDbContext`. A pending approval from such a
session then resolves as *unbound* and runs with no narrowing at all.

Every other case is covered: agent still visible → its scope; agent present but no longer
visible to the caller → deny-all. Pinned by
`LmKitOmniApi.Tests/ApprovedActionScopeTests.ApprovedAction_FallsBackToUnscoped_WhenTheBoundAgentWasDeleted`
so it cannot change silently.

**Fix:** snapshot the narrowing fields onto the approval row when it is created (a
`RequestOptionsJson` column on `TaskApproval` + a migration), and prefer the snapshot over the
session walk.

---

## 3. Guardrail patterns are `\b`-anchored, so a secret glued to a word is missed

`LmKitOmniApi/Infrastructure/AI/Security/PromptGuardService.cs:118` (SSN) and the equivalent
SSN/email patterns in `Infrastructure/AI/Filters/OutputGuardrailFilter.cs`.

`123-45-6789The` and `bob@example.comThe` match neither the detector nor the redactor — in the
streaming path *and* in the non-streaming one. Real model output separates tokens, so this has
not been observed in practice.

Recorded so nobody rediscovers it as a "streaming leak". **Deliberately not pinned by a test:**
asserting the weakness would make it harder to remove.

---

## 4. The rolling summary is merged into the user's next turn

`LmKitOmniApi/Application/Chat/Handlers/StreamChatCommandHandler.cs:148`

```csharp
else if (msg.Role == "system") history.AddMessage(AuthorRole.User, msg.Content); // Inject summary as user context
```

LM-Kit's `ChatHistory` merges consecutive same-role turns with a newline, so the summary does
not arrive as its own block — the model receives one user turn containing
`"<summary>\n<first real user turn>"` and cannot tell the two apart.

Probably benign, but invisible from the `AddMessage` loop. Pinned by
`StreamChatCommandHandlerTests.History_IsLoadedChronologically_TrimmedByTheTokenService_AndItsSummaryIsInjectedAndStored`
so a change to it is deliberate.

**Fix if it matters:** wrap the summary in a labelled delimiter rather than emitting it bare.

---

## 5. Approving from the Approvals page loses the result for chat sessions

`LmKitOmniClient/src/views/approvals/ApprovalsView.vue`

Chat drives its own continuation client-side: `useHitlActions` pushes the approved tool result
back as the next user turn. That works from the chat card. Approving the *same* task from the
Approvals page writes nothing to the conversation — the tool output exists only in that page's
HTTP response and is then lost.

**Fix:** either persist an assistant turn for chat approvals server-side, or have
`ApprovalsView` navigate into the session and continue the turn there.

---

## 6. The agent-run page shows the approval id but cannot act on it

`LmKitOmniClient/src/views/agents/AgentRunsView.vue:387,587`

The view captures the approval id from the `[HITL_APPROVAL_REQUIRED:]` marker into
`approvalTaskId` and then does nothing with it — the user has to go find the approval on the
Approvals page. `statusMeta()` also has no case for the `CompletedAfterApproval` / `Rejected`
statuses, so they render as untranslated raw strings in the default grey pill.

**Fix:** surface approve/reject inline (reuse `useHitlActions`), re-poll
`GET /api/agent-runs/{id}` afterwards, and add the two status labels.

---

## 7. A run that ends at an approval gate does not resume the ReAct loop

By design, recorded here so it is not mistaken for a bug. An approved gated call is recorded as
a real `AgentRunStep`, its output is appended to `AgentRun.Result` and to the run's hidden
session, and the run moves to the terminal status `CompletedAfterApproval`
(`LmKitOmniApi/Application/AgentRuns/AgentRunStatuses.cs`).

It is **not** a resume. Feeding the approved observation back into the loop would need a
durable continuation of the streaming pass — the orchestrator's ReAct pass is an in-process
`await foreach` whose state dies with the request. That is a redesign, not a fix. A run that
ends after one approved tool call is a smaller answer than a resumed run would give, but it is
a truthful one, and it ends.

---

## 8. The live voice agent serves exactly one configured user

`LmKitOmniApi/Infrastructure/AI/Voice/VoiceRoomAgentHostedService.cs`

Rooms are scoped per user: `VoiceRoomNaming.TryScopedRoom` produces `{tenant:N}-{user:N}-{label}`
and both `GET /api/speech/token` and the hosted agent call it, so the agent and its caller land
in the same room. But the hosted service is a single `BackgroundService` that mints one token,
joins one room, and runs one session loop for the lifetime of the process — it has no way to
learn that another user just opened a room. With `Voice:LiveAgentEnabled=true` and no
`Voice:AgentTenantId` / `Voice:AgentUserId` it stands down with an explicit warning rather than
joining a room nobody is in.

Serving every user from one deployment needs a **room dispatcher**, which is a redesign:
discover rooms (LiveKit `room_started` / `participant_joined` webhooks, or poll
`RoomServiceClient.ListRooms`), own a session per room keyed by room name, bound it with a
max-concurrent-rooms cap and per-room idle timeout (each live room holds a model lease during a
turn), and add a per-user opt-in — the dispatcher would otherwise join rooms on behalf of users
who never consented to an agent participant.

`docker-compose.prod.yml` does not currently pass `Voice__AgentTenantId` / `Voice__AgentUserId`
through, so a compose deployment cannot configure the agent without editing the file.

---

## 9. The default chat model is not shipped and its filename looks wrong

`LmKitOmniApi/appsettings.json:49,60`

`AiModels:DefaultChat = "bonsai"` resolves to `AIModels/bonsai/Bonsai-27B-Q1_0.gguf`.
`*AIModels/` is gitignored (`.gitignore:89`) and there is no download script, so **a fresh
checkout has no chat model** and `/health/ready` reports Unhealthy until one is placed there.
That is intended — the previous behaviour reported healthy and failed on the first user
message.

Separately, `Q1_0` is not a llama.cpp quantization tag (it ships `Q2_K`, `IQ1_S`, `IQ1_M`, …),
so this exact filename most likely never existed. Picking the shipped default is a product
decision, so it is recorded rather than changed.

---

## 10. Stale cross-references to deleted `*-INTEGRATION.md` files

Source comments point at handoff files that no longer exist:

| File | Refers to |
| --- | --- |
| `LmKitOmniApi/Controllers/ComputerUseController.cs:22` | `COMPUTER-USE-INTEGRATION.md` |
| `LmKitOmniApi/Controllers/GroundingEvalController.cs:21` | `GROUNDING-EVAL-INTEGRATION.md` |
| `LmKitOmniApi/Controllers/GroundingTrainingController.cs:18` | `TRAINING-INTEGRATION.md` |
| `LmKitOmniApi/Controllers/WidgetPublicController.cs:38` | `WIDGET-FIX-INTEGRATION.md` |
| `LmKitOmniApi/Application/AgentRuns/AgentRunStatuses.cs:21` | `AGENTRUN-FIX-INTEGRATION.md` |
| `LmKitOmniApi/Infrastructure/AI/AgentOrchestrator.cs:1056` | `CORE-FIX-INTEGRATION.md` |
| `LmKitOmniApi/Infrastructure/AI/Voice/VoiceOptions.cs:89` | `VOICE-CU-FIX-INTEGRATION.md` |
| `LmKitOmniApi/Infrastructure/AI/Voice/VoiceRoomAgentHostedService.cs:30` | `VOICE-CU-FIX-INTEGRATION.md` |
| `LmKitOmniApi/Infrastructure/AI/Voice/VoiceRoomNaming.cs:23` | `VOICE-CU-FIX-INTEGRATION.md` |
| `LmKitOmniApi.Tests/ApprovedActionScopeTests.cs:185` | `CORE-FIX-INTEGRATION.md` |
| `LmKitOmniApi.Tests/DocumentsControllerTests.cs:33,310,376` | `PDF-INTEGRATION.md` |
| `LmKitOmniApi.Tests/StreamingGuardrailGateTests.cs:232` | `AGENTRUN-FIX-INTEGRATION.md` |

Several of those controller comments also claim the feature "works without touching
`Program.cs` / `appsettings.json`" — the wiring has since landed in both files, so the caveat is
stale as well.

**Fix:** point the comments at this file (or at `ai-agent-capabilities.md`), or delete them.

---

## 11. `/hubs` is proxied but no SignalR hub exists

`LmKitOmniClient/vite.config.ts:27` and `LmKitOmniClient/nginx.conf:62` both proxy `/hubs/` to
the API, but the API registers no hub: `AddSignalR` and `MapHub` appear nowhere in
`LmKitOmniApi/`. The SignalR echo surface was removed and the proxy rules were left behind.
Streaming is SSE only.

**Fix:** delete both proxy blocks.
