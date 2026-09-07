# Voice + Computer-Use defect fixes — integration notes

The code fixes for T9 (voice room / credentials / media session) and T10 (computer-use `key`
action, safety markers, native handle leaks, time budgets) are complete and self-contained.
This file holds the **configuration changes an operator must apply**, because
`Program.cs` and `appsettings.json` are owned elsewhere and were deliberately not edited.

**DI: nothing to change.** No new services were introduced. `VoiceRoomNaming` and
`VoiceLiveKitCredentials` are pure static helpers, and `VoiceRoomAgentHostedService` now takes
an `IConfiguration` — which ASP.NET Core registers by default — so the existing
`builder.Services.AddHostedService<VoiceRoomAgentHostedService>()` registration keeps working
unchanged.

---

## 1. `appsettings.json` — `ComputerUse` time budgets (REQUIRED)

`SessionWallClockSeconds: 300` and `ApprovalTimeoutSeconds: 300` are mutually impossible: the
session is cancelled at exactly the moment ONE approval is still allowed to be pending, so any
run that asks a human anything can never complete. The C# defaults are fixed, but
`appsettings.json` sets these explicitly and therefore still overrides them.

The shipped invariant is
`SessionWallClockSeconds >= MaxSteps * (StepTimeoutSeconds + ApprovalTimeoutSeconds)`
(`ComputerUseOptions.AreTimeBudgetsConsistent`). With the shipped step cap that is
`15 * (30 + 90) = 1800`.

```jsonc
  "ComputerUse": {
    "Enabled": false,
    "Image": "",
    "RuntimePath": "docker",
    "NetworkName": "",
    "AllowedHosts": [],
    "MaxSteps": 15,
    "StepTimeoutSeconds": 30,
    "SessionWallClockSeconds": 1800,   // was 300 — could not cover even one approval
    "MemoryMb": 512,
    "Cpus": 1.0,
    "PidsLimit": 512,
    "MaxScreenshotBytes": 5242880,
    "MaxElements": 100,
    "RequireApprovalPerAction": true,
    "ApprovalTimeoutSeconds": 90       // was 300 — equal to the whole session budget
  },
```

Until this is applied the agent logs a one-time warning at the start of the first run naming
both numbers and the worst case, instead of silently dying at the wall clock.

---

## 2. `appsettings.json` — `Voice` room + agent identity (REQUIRED to use the live agent)

`Voice:Room` is now a room **label**, not a room name. The real room is always
`{tenant:N}-{user:N}-{label}`, produced by `VoiceRoomNaming` — the same function the browser
token endpoint uses. Because rooms are scoped per user, the single hosted agent must be told
**whose** room it joins.

```jsonc
  "Voice": {
    "TtsEnabled": false,
    "LiveAgentEnabled": false,
    "DefaultVoice": "default",
    "MaxSynthesisCharacters": 2000,
    "PiperExecutablePath": "",
    "PiperVoices": {},
    "SynthesisTimeoutSeconds": 30,
    "LiveKitUrl": "",
    "LiveKitApiKey": "",
    "LiveKitApiSecret": "",
    "Room": "omni-room",               // now a LABEL: the room is {tenant}-{user}-omni-room
    "AgentIdentity": "voice-agent",
    "AgentTenantId": "",               // NEW — GUID of the tenant whose room the agent joins
    "AgentUserId": ""                  // NEW — GUID of the user  whose room the agent joins
  }
```

With `LiveAgentEnabled: true` but no `AgentTenantId`/`AgentUserId`, the hosted service now
**stands down with an explicit warning** rather than joining a bare `omni-room` that no caller
will ever be in (the previous behaviour: the agent was permanently in a different room from
every caller).

To find the right values, call `GET /api/speech/token` as the target user — the response now
echoes the room it minted:

```json
{ "token": "…", "room": "1111…1111-2222…2222-omni-room" }
```

The prefix segments are that user's tenant id and user id in `N` (32 hex, no dashes) format.

### `docker-compose.prod.yml`

```yaml
  - Voice__AgentTenantId=${VOICE_AGENT_TENANT_ID:-}
  - Voice__AgentUserId=${VOICE_AGENT_USER_ID:-}
```

---

## 3. LiveKit credentials — one source, no config change required

The token endpoint used to read `LiveKit:ApiKey` / `LiveKit:ApiSecret` while the hosted agent
read `Voice:LiveKitApiKey` / `Voice:LiveKitApiSecret`; setting one did nothing for the other.

`VoiceLiveKitCredentials.Resolve` is now the single source for **both**, resolved per field:

1. `Voice:LiveKitUrl` / `Voice:LiveKitApiKey` / `Voice:LiveKitApiSecret` (voice-specific), then
2. `LiveKit:Url` / `LiveKit:ApiKey` / `LiveKit:ApiSecret` (the shared block that also configures
   the LiveKit container).

Existing deployments need no change — `docker-compose.prod.yml` already feeds both blocks from
the same `LIVEKIT_API_KEY` / `LIVEKIT_API_SECRET`. New deployments can set **either** block.

---

## 4. Per-session voice rooms: what is and is not possible today

**Room naming is now per-user and is achieved without any redesign.** One function
(`VoiceRoomNaming.TryScopedRoom`) produces `{tenant:N}-{user:N}-{label}`, and both the token
endpoint and the hosted agent call it, so:

* the agent and its caller are in the same room (they never were before), and
* two colleagues in one tenant asking for the same label get **different** rooms and can no
  longer hear each other. A caller may pass a distinct `?room=` label for additional
  concurrent rooms; the label cannot widen the scope, since the tenant/user prefix is
  server-supplied and the label is sanitized to `[A-Za-z0-9_-]`.

**Serving many users at once is NOT possible with the current hosted-service design and does
need a redesign.** `VoiceRoomAgentHostedService` is a single `BackgroundService` that mints one
token, joins one room, and runs one `RunSessionAsync` loop for the lifetime of the process. It
has no way to learn that some other user just opened a room. Making one deployment serve every
user requires a **room dispatcher**, roughly:

1. Discover rooms — subscribe to LiveKit webhooks (`room_started` / `participant_joined`) or
   poll `RoomServiceClient.ListRooms` for names matching the `{tenant}-{user}-` shape.
2. Own a session per room — a keyed map of room name → `CancellationTokenSource` +
   `ILiveKitMediaSession` + `IVoiceRoomAgent` (both already resolved per scope, so the seams
   are fine), spawning `RunSessionAsync` per room and disposing it on `room_finished`.
3. Bound it — a max-concurrent-rooms cap and a per-room idle timeout, since each live room
   holds a model lease during a turn.
4. Authorize it — the dispatcher joins rooms on behalf of users who never individually
   consented to an agent participant; that needs a per-user opt-in the current options model
   has no place for.

None of that is faked here. What ships is: correct, shared, per-user room naming; a hosted
agent that joins the *right* room for one configured user; and an explicit stand-down (not a
silent no-op) when it has not been told which user that is.

---

## 5. Behaviour changes worth knowing about

* **`GET /api/speech/token`** now returns `{ token, room }` (was `{ token }`). Additive; the
  Vue client reads only `token` and is unchanged.
* **The `key` action now requires a `ref`.** `{"action":"key","keys":"Enter"}` is rejected by
  the parser; `{"action":"key","ref":3,"keys":"Enter"}` is accepted, grounded, safety-checked
  and approval-gated. A rejected key press now costs one step and a corrective re-ask instead
  of ending the session. Any interactive-browser container image must therefore honour an
  optional `"ref"` field on the `key` wire payload (focus that element, then dispatch the
  keys); an image that ignores `ref` keeps its previous "send to the focused element"
  behaviour and stays compatible.
* **Safety markers**: short acronyms (`pin`, `otp`, `cvv`, `cvc`, `ssn`, `pwd`, `2fa`, `mfa`)
  now match on letter boundaries. "Shipping address", "Pinterest" and "spinner" no longer
  terminate a session; "PIN", "Mã PIN", "CVV2" and "pin_code" still do. Longer markers
  (`password`, `card number`, `captcha`, …) remain substring matches.
* **`key` is now credential-checked like `type`**, so keystrokes can never be used to enter a
  secret one character at a time into a password/OTP field.
