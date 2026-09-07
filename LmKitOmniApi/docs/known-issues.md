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
not acting on its approval · **#7** a run parked on an approval gate now continues its ReAct
loop · **#10** the dead `/hubs` proxy.

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

## 8. The multi-room voice agent is off by default and its media path is unproven

`LmKitOmniApi/Infrastructure/AI/Voice/VoiceRoomDispatcher.cs`

Live voice used to serve exactly one hard-configured user per deployment: the hosted service was
a single `BackgroundService` that joined one room for the life of the process. It now has a
second mode — `Voice:DispatcherEnabled` — that owns one bounded session per room whose owner
explicitly opted in via `GET /api/speech/token?agent=true`, with caps on concurrent rooms, rooms
per tenant, and (the one that matters, given `SemaphoreLimits:Chat = Speech = 1`) concurrent
turns, plus a per-room idle timeout and per-turn budget.

Rooms are NOT discovered from LiveKit. Webhooks need a public endpoint this deployment does not
have, and `RoomServiceClient.ListRooms` would say a room exists without saying whether its owner
wants a listener in it. Consent is the room list.

What remains open:

- **It is off by default** (`Voice:DispatcherEnabled=false`). Turning it on also needs
  `Voice:LiveAgentEnabled=true` and a reachable LiveKit.
- **The LiveKit media path is still unverified.** `LiveKitMediaSession` needs a live server and
  the native `livekit_ffi` runtime, so the suite proves the dispatcher's caps, consent, fairness,
  reclamation, lease release and shutdown with fakes, and proves nothing below
  `ILiveKitMediaSession`. In particular, that one process can hold several agent participants in
  different rooms simultaneously has not been observed.
- **The consent ledger is process-local.** Whichever replica minted the token dispatches the agent
  — one agent per room, no coordination needed — but a busy replica cannot hand a room to a
  quieter one; that user simply gets no agent.
- **`MaxConcurrentTurns` defaults to 1**, so several callers talking at once queue behind one
  another. Raising it without raising `SemaphoreLimits:Chat`/`Speech` does nothing.

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

## 11. The opt-in live model tests are a probe, not a gate

`LmKitOmniApi.Tests/ChatConversationFactoryLiveTests.cs`,
`LmKitOmniApi.Tests/AgentRunResumeLiveTests.cs`

Everything behind `LMKIT_LIVE_SEAM_TEST=1` loads real weights and skips cleanly without them, so
none of it runs in CI. Within it, two assertions are about **model behaviour** — whether a 1B
chooses to invoke a registered tool on turn 3, and whether it repeats a number from a replayed
observation. Neither is deterministic. Both take several attempts and one skips rather than
fails when the model never called the tool even on turn 1 (a model too weak to testify is not
evidence of a regression), but on a busy machine they still fail intermittently: observed green
when run one class at a time on an idle machine, and one failure per run while four other agents
were building.

**Run them deliberately, on an otherwise idle machine, and read a failure as "the model did not
cooperate this run" before reading it as a regression.** The properties they illustrate are each
guarded deterministically elsewhere and those guards are the real gate:

- the tool catalog reaching a rebuilt history —
  `NonEmptyHistory_ToolCatalogIsSeededOnlyWhenToolsArePassedToTheFactory` (no sampling: the tool
  definitions are present with the fix and absent without);
- the system prompt reaching the rendered prompt — `NonEmptyHistory_SystemPromptReachesTheRenderedPrompt`;
- chat answering at all, rather than `[ERROR]: Unable to generate a response.` —
  `LiveChatSecondTurnTests`, which deliberately asserts plumbing and not obedience, and has been
  stable.

---
