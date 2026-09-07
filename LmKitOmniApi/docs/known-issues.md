# Known issues — diagnosed, not fixed

The single place this repo tracks defects that have been **located in code** but deliberately
left unfixed. Every entry names the file and the reason it is still open. Fix an entry →
delete it from this file in the same change.

Not a backlog of ideas. Something only belongs here once someone has read the code and can
point at the line.

Numbers are **stable ids, not positions**: a gap means that entry was fixed and deleted. Never
renumber — other files link to these anchors, and renumbering is how such links go stale.
Fixed so far: **#0** (the widget frame-ancestors CSP, fixed in `nginx.conf`) and **#1**
(`ToolSandboxService` now resolves its roots per instance instead of at type-init).

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

`docker-compose.prod.yml` now passes `Voice__AgentTenantId` / `Voice__AgentUserId` through as
`VOICE_AGENT_TENANT_ID` / `VOICE_AGENT_USER_ID`, so configuring the single-user agent no longer
requires editing the compose file. The dispatcher redesign above is still open.

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

## 10. `/hubs` is proxied but no SignalR hub exists

`LmKitOmniClient/vite.config.ts:27` and `LmKitOmniClient/nginx.conf:62` both proxy `/hubs/` to
the API, but the API registers no hub: `AddSignalR` and `MapHub` appear nowhere in
`LmKitOmniApi/`. The SignalR echo surface was removed and the proxy rules were left behind.
Streaming is SSE only.

**Fix:** delete both proxy blocks.
