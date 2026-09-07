# Known issues — diagnosed, not fixed

The single place this repo tracks defects that have been **located in code** but deliberately
left unfixed. Every entry names the file and the reason it is still open. Fix an entry →
delete it from this file in the same change.

Not a backlog of ideas. Something only belongs here once someone has read the code and can
point at the line.

Numbers are **stable ids, not positions**: a gap means that entry was fixed and deleted. Never
renumber — other files link to these anchors, and renumbering is how such links go stale.
Fixed so far: **#0** widget frame-ancestors CSP · **#1** `ToolSandboxService` root pinning ·
**#2** approval scope after its custom agent is deleted · **#3** guardrail patterns glued to a
word · **#5** approving from the Approvals page losing the result · **#6** the agent-run page
not acting on its approval · **#10** the dead `/hubs` proxy.

---

## 4. TRAP — the injected summary must stay `AuthorRole.User`

`LmKitOmniApi/Application/Chat/Handlers/StreamChatCommandHandler.cs`

Not a defect: a landmine for whoever "fixes" it next. This entry used to claim the summary
should be a System turn. **It must not be.**

`ChatHistory` merges consecutive same-role turns with a newline, so the summary genuinely does
arrive glued to the first real user turn (verified with a probe against real LM-Kit). The
obvious fix — inject it as `AuthorRole.System` — silently DELETES it: decompiling
`MultiTurnConversation` shows that constructing on a non-empty history adopts `Messages[0]` as
`SystemPrompt` when it is a System message, and `AgentOrchestrator` overwrites
`chat.SystemPrompt` on the very next line. `WidgetInferenceSession` follows the same rule.

The boundary problem is addressed instead: the summary now carries an explicit end marker, and
a test pins "never `AuthorRole.System`". Keep it that way.

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

