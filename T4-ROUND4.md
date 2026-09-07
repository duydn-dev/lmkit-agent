# T4 — round 4 follow-ups

Worktree `D:\GitHubs\lmkit-agent-wt\t4-followups`, branch `round4/t4-followups`, based on
master `c999f12`. Nothing outside my owned file list was changed in the commit.

---

## Item 1 — `PendingApprovalDto` has no `ChatSessionId`

### What I found when I verified it

S3's report is accurate as far as it goes, but the situation is **larger** than
"it's already on the row and already selected from". The raw id alone cannot replace the
search, and the search is doing real filtering work today.

- `TaskApproval.ChatSessionId` exists, is non-nullable, and its FK is
  `DeleteBehavior.Cascade` (`HermesDbContext.cs:206`) — so every pending approval has a
  live session. The handler does **not** currently select it
  (`GetPendingApprovalsQueryHandler.cs:36`).
- **But** the session it names is not always a conversation. Both approval creation sites
  write a session id that can be a *hidden substrate*:
  - `AgentOrchestrator.cs:882` — `ChatSessionId = sessionId`, where an agent run's session
    is created with `IsAgentRun = true` (`StreamAgentRunCommandHandler.cs:60`);
  - `ComputerUseApprovalGate.cs:290` — whose session is created two lines earlier with
    `IsAgentRun = true, // hidden substrate — never shows in the chat list`.
- The search endpoint filters `!s.IsAgentRun && !s.IsEphemeral`
  (`GetChatSessionsSearchQueryHandler.cs:34`). So the "fragile" content search is
  *also* the thing that stops the Approvals page from posting an approval continuation
  into a hidden agent-run substrate — a session that resolves through
  `AgentRunApprovalReconciler`, and that the "Mở cuộc trò chuyện" link would send the user
  to even though it never appears in their chat list. The page's `skipped` copy already
  names exactly those two cases.
- There is **no by-id session endpoint** (`ChatController` has `sessions`,
  `sessions/{id}/messages`, `sessions/search`, and nothing else), so a client holding only
  a GUID cannot find out whether it is a chat, nor what it is called.

**So: the case the direct id genuinely does not cover is `IsAgentRun` / `IsEphemeral`.**
That is why the server diff below adds a flag and a title alongside the id, rather than the
id alone — with all three, the search workaround becomes genuinely redundant and the
fallback can be deleted outright.

### What I did

Client only (T3 owns `Application/Approvals/**`).

- `useTaskApproval.ts`: new `ApprovalSessionFields` (the three optional DTO fields),
  `approvalChatSessionFromRow()` (pure: session / `null` = "no conversation" /
  `undefined` = "server didn't say"), and `resolveApprovalChatSession()` — the one call a
  surface makes. `findApprovalChatSession` is kept and re-documented as **TRANSITIONAL**:
  reached only when the row omits the fields, with the deletion condition written down.
- `ApprovalsView.vue`: `PendingApproval` grows the three optional fields, `toRow` carries
  them, and `writeResultToConversation` calls `resolveApprovalChatSession(row)`.

Guarded exactly as instructed: with today's API (no fields) the page behaves identically to
before; with the diff applied it makes **no network call at all** to find the session, and
answers an agent-run/ephemeral approval as `skipped` without a speculative search.

A row that claims `isChatSession: true` but carries a blank/`Guid.Empty`/malformed id is
treated as "server didn't say" rather than trusted — half a truth is not a session to post
an approval result into.

### What proves it

- 8 new unit tests in `useTaskApproval.test.ts` covering: read-from-row, missing title,
  `isChatSession:false` → null with **no fetch**, fields absent → falls back to the search
  with the exact URL, and the five self-contradictory id shapes.
- `npm run test:unit` 89 passed (baseline 81).
- `npm run build` runs `vue-tsc -b`, so the view is type-checked; clean.
- I applied the server diff below **temporarily** in this worktree, ran a scratch handler
  test against real SQLite (`EnsureCreated`, real SQL, three approvals: chat / agent-run /
  ephemeral), confirmed the EF projection translates and returns the right values for all
  three, confirmed `ApprovalDetailsTests` + `ApprovalExpiryTests` still pass (21/21), then
  reverted. The working tree's `git diff` md5 is byte-identical before and after that
  exercise (`3e85004f52a5438d2e89600337bb75f4`), so nothing of T3's leaked into the commit.

### (a) The exact server-side diff for you to apply

Both hunks are verified to compile and to translate to SQL on SQLite.

```diff
diff --git a/LmKitOmniApi/Application/Approvals/Queries/GetPendingApprovalsQuery.cs b/LmKitOmniApi/Application/Approvals/Queries/GetPendingApprovalsQuery.cs
--- a/LmKitOmniApi/Application/Approvals/Queries/GetPendingApprovalsQuery.cs
+++ b/LmKitOmniApi/Application/Approvals/Queries/GetPendingApprovalsQuery.cs
@@ -36,4 +36,36 @@ public class PendingApprovalDto
     /// closes; this round only makes the API honest and does not change the UI.
     /// </summary>
     public DateTime ExpiresAtUtc { get; set; }
+
+    /// <summary>
+    /// The chat session this approval was raised in — <c>TaskApproval.ChatSessionId</c>,
+    /// straight off the row and already selected from. Carried so a surface that approves
+    /// outside the originating chat (the Approvals page) can write the tool output back into
+    /// the conversation that asked for it, instead of rediscovering the session by
+    /// full-text-searching message content for this approval's GUID — a lookup that depends
+    /// on the <c>[HITL_APPROVAL_REQUIRED:{id}]</c> marker surviving verbatim in a persisted
+    /// message.
+    /// </summary>
+    public Guid ChatSessionId { get; set; }
+
+    /// <summary>
+    /// True when <see cref="ChatSessionId"/> names a session the user can actually open and
+    /// continue. False for the two substrates that carry a REAL <see cref="ChatSessionId"/>
+    /// but are excluded from every chat list and search
+    /// (<c>GetChatSessionsQueryHandler</c>, <c>GetChatSessionsSearchQueryHandler</c>): the
+    /// hidden <c>IsAgentRun</c> session behind an agent run or a computer-use gate — those
+    /// resolve through <c>AgentRunApprovalReconciler</c> and belong on the agent-run page —
+    /// and a temporary (<c>IsEphemeral</c>) chat, which persists nothing to continue.
+    ///
+    /// <para>This flag is why the id alone does not replace the search: without it a client
+    /// would post an approval continuation into a hidden substrate and then link the user to
+    /// a session that never appears in their chat list.</para>
+    /// </summary>
+    public bool IsChatSession { get; set; }
+
+    /// <summary>
+    /// Title of that session, so a client can name the conversation it wrote into. Empty
+    /// unless <see cref="IsChatSession"/>.
+    /// </summary>
+    public string ChatSessionTitle { get; set; } = string.Empty;
 }
diff --git a/LmKitOmniApi/Application/Approvals/Handlers/GetPendingApprovalsQueryHandler.cs b/LmKitOmniApi/Application/Approvals/Handlers/GetPendingApprovalsQueryHandler.cs
--- a/LmKitOmniApi/Application/Approvals/Handlers/GetPendingApprovalsQueryHandler.cs
+++ b/LmKitOmniApi/Application/Approvals/Handlers/GetPendingApprovalsQueryHandler.cs
@@ -33,7 +33,22 @@ public class GetPendingApprovalsQueryHandler : IRequestHandler<GetPendingApprova
                 && t.Status == "Pending"
                 && t.ExpiresAtUtc > now)
             .OrderByDescending(t => t.CreatedAtUtc)
-            .Select(t => new { t.Id, t.ActionName, t.ParametersJson, t.CreatedAtUtc, t.ExpiresAtUtc })
+            .Select(t => new
+            {
+                t.Id,
+                t.ActionName,
+                t.ParametersJson,
+                t.CreatedAtUtc,
+                t.ExpiresAtUtc,
+                t.ChatSessionId,
+                // LEFT JOIN through the navigation. The FK is DeleteBehavior.Cascade, so a
+                // null session means the row is on its way out; treat that as "not a
+                // conversation" rather than assuming it is one.
+                IsChatSession = t.ChatSession != null
+                    && !t.ChatSession.IsAgentRun
+                    && !t.ChatSession.IsEphemeral,
+                ChatSessionTitle = t.ChatSession != null ? t.ChatSession.Title : null
+            })
             .ToListAsync(cancellationToken);
 
         return rows.Select(t => new PendingApprovalDto
@@ -42,7 +57,10 @@ public class GetPendingApprovalsQueryHandler : IRequestHandler<GetPendingApprova
             ActionName = t.ActionName,
             Details = Describe(t.ParametersJson),
             CreatedAtUtc = t.CreatedAtUtc,
-            ExpiresAtUtc = t.ExpiresAtUtc
+            ExpiresAtUtc = t.ExpiresAtUtc,
+            ChatSessionId = t.ChatSessionId,
+            IsChatSession = t.IsChatSession,
+            ChatSessionTitle = t.IsChatSession ? (t.ChatSessionTitle ?? string.Empty) : string.Empty
         }).ToList();
     }
```

Notes for applying it:

- New properties are appended, so the DTO's "declaration order mirrors the previous
  anonymous projection" comment still holds and the existing JSON shape is unchanged.
- ASP.NET Core camel-cases these: `chatSessionId`, `isChatSession`, `chatSessionTitle` —
  the names the client reads.
- **Once applied**, delete `findApprovalChatSession` and the `if (fromRow !== undefined)`
  branch in `resolveApprovalChatSession` (both in
  `LmKitOmniClient/src/composables/useTaskApproval.ts`), plus the
  `describe('findApprovalChatSession')` block and the
  `'degrades to the content search against a server without the fields'` case in
  `useTaskApproval.test.ts`, and drop `CHAT.SEARCH_SESSIONS` if nothing else uses it
  (nothing else does today). The comments in the code say this too.
- If T3's approval-resume redesign moves the continuation server-side, this whole
  client-driven path may become dead; the diff is still the right shape for anything that
  needs to know an approval's conversation.

---

## Item 2 — dead `/hubs` reference in the service worker

### What I found

Confirmed inert. Repo-wide, `AddSignalR|MapHub|Microsoft.AspNetCore.SignalR` across
`LmKitOmniApi/**` returns **0 hits**. The only surviving `/hubs` mentions were the two in
`sw.js` plus explanatory comments in `nginx.conf:89` and `vite.config.ts:27` recording that
round 3 deleted those proxies. No client code requests a `/hubs/*` URL
(`api.factory.ts` has no non-`/api` path at all).

### What I did

Removed the bypass (`sw.js:68`) and rewrote the strategy comment, recording why the
reference existed and why it is gone. Verified the built artefact: `dist/sw.js` has no
`/hubs` branch left, only the explanatory comment.

I deliberately did **not** bump `CACHE_NAME`. The file's own rule says to bump it "whenever
the cache strategy changes", but this change is unobservable — no `/hubs/*` request is ever
issued, and if one were, `networkFirst` would get a 404 and only 200s are cached. Bumping
would purge every user's cache for no behavioural difference, and would additionally create
the classic "old page requests an old chunk that was just purged" window, which the current
stable name avoids.

### Reading the whole file — what else is there

Two things worth recording, neither fixed (neither is clearly wrong):

1. **No offline navigation fallback.** `networkFirst` caches per-URL and rethrows when the
   network fails and that exact URL was never cached. An offline user navigating to a route
   they have not visited gets the browser error page rather than the cached app shell. A
   `request.mode === 'navigate'` fallback to a cached `/index.html` is the usual fix; it is
   a feature decision, not a defect, so I left it.
2. **Every SPA route gets its own cache entry.** History-fallback means `/chat`,
   `/approvals`, … all return the same `index.html` and each is cached separately. Bloat,
   not incorrectness.

On the "stale build" question specifically the file is in good shape and I found nothing
wrong: `skipWaiting()` + `clients.claim()` + network-first HTML is the correct anti-stale
posture, `/assets/*` is genuinely immutable (Vite content hashes), and because
`CACHE_NAME` is stable the `activate` sweep never deletes chunks an already-loaded page
still needs.

---

## Item 3 — derived test hosts inherit the parent's data-protection key path

### What I found

Real, and I reproduced it directly rather than inferring it. With master's code, a scratch
test asserting `parentConfig["DataProtection:KeyPath"] != derivedConfig[...]` fails with
`Assert.NotEqual() Failure: Strings are equal` — the two running hosts report the **same**
directory. `RateLimitTestHost.Create` is the only `WithWebHostBuilder` call in the suite;
`WithWebHostBuilder` replays the parent's `ConfigureWebHost` (which writes the parent's
path) and then layers overrides that never mention key storage.

I'd also call it slightly *worse* than "latent": the parent is a long-lived class fixture
whose host stays up while the derived host serves requests, so any derived host that
performs a protected operation is already a second live host on one key ring. It has not
bitten because the derived hosts are few and short-lived.

### What I did

- `TestHostConfiguration`: added `DataProtectionKeyPathSetting`,
  `NewDataProtectionKeyPath(parentKeyPath = null)` and `ApplyDerived(builder,
  parentKeyPath, settings)` — identical to `Apply` except it mints a key path when the
  caller did not name one. Both factories now mint through the helper.
- A derived ring is **nested inside** the parent's directory. That is deliberate and buys
  two things at once: `FileSystemXmlRepository` enumerates `*.xml` in its own directory
  only (never a subdirectory), so the rings are as isolated as siblings would be; and the
  parent's existing `Directory.Delete(path, recursive: true)` on `Dispose` cleans up every
  derived ring, so no derived host leaks a temp directory.
- `LmKitApiFactory` / `DocumentsApiFactoryBase` expose `DataProtectionKeyPath` internally
  so a derived host can nest under it.
- `RateLimitTestHost.Create` calls `ApplyDerived`, reading the parent path **outside** the
  callback so the ring is fixed per derived factory rather than per host build.

### What proves it

`LmKitOmniApi.Tests/DerivedTestHostIsolationTests.cs` — four tests, asserted through the
**running host's** `IConfiguration` and through real protected operations, not by
inspecting the helper:

1. a derived host's key path differs from its parent's;
2. two derived hosts of one parent differ from each other, **and** their own overrides
   still land (isolating the ring did not cost the thing a derived host exists for);
3. the derived ring is nested under the parent's, which is what makes cleanup work;
4. parent + two derived hosts all complete a real login (an auth cookie is produced by the
   data-protection stack) concurrently while all three are live — the exact shape that
   produced `Fixture login failed: InternalServerError`.

Plus: **6 full-suite runs, 4 of them at `MaxParallelThreads=32`**, all green (counts below).
One green run proves nothing here, agreed; six do not prove absence either, but combined
with the direct configuration assertions the mechanism is closed, not just unobserved.

---

## Item 4 — prose about credentials stops streaming mid-answer

I did change the gate. The invariant holds and I can argue it; the property suite is green
and extended.

### What I found

Reproduced exactly, and it is worse than "a latency problem". On master, feeding
`"A bearer token is only a header value, never a secret in itself." + 60 words of prose`
one character at a time through the gate emits **2 of 346 characters**. `"Never store a
password in plain text."` emits 14 of 319. The rest arrives on the end-of-stream flush.

Mechanism: `CapBeforeEarliestMatch(view, CredentialHoldPattern, safeLength)` searches from
`_emitted.Length`, so once the cap lands on a keyword the next attempt finds the same
keyword at the same index. An **unlatched** keyword pins emission there permanently, and
prose about credentials never latches the class because it contains no credential span.

### What I did

Replaced the keyword-only cap with an **emission boundary** measured on `raw` with the real
`CredentialRedactionPattern`, deleted `CredentialHoldPattern`, and rewrote the class remarks.
Step ordering inside `AppendAndTryEmitAsync` is now: hold boundary → **latch** →
**emission boundary** → view → cap → prefix check.

The latch deliberately still runs against the *full* settled region, before the boundary
narrows: the credential span the boundary stops in front of is exactly the one the detector
must see in order to latch the class and let emission move past it. Narrowing first would
starve the detector of its own trigger and deadlock the stream at that index.

### (c) The invariant, stated precisely, and why it is sound

> **Emission boundary.** Let `raw` be the accumulated output, `L = |raw|`, and `S = L − d`
> the settled boundary from the existing hold rule. Let `C` be the start index of the first
> `CredentialRedactionPattern` match in `raw` while the credential class is unlatched, and
> `C = L` once it is latched. The gate releases exactly the image of `E = min(S, C)` under
> the view transforms. Concatenated emitted chunks plus the end-of-stream remainder equal
> the guardrail-processed response byte for byte, and the emitted text is always a genuine
> prefix of it.

Soundness, in three steps:

1. **Nothing new can appear below `S`.** The hold rule computes `d` so that for every
   pattern `P` and every index `s < S`, `raw[s..L]` is not a viable prefix of a `P`-match.
   So a match beginning below `S` in any continuation of the stream must already be complete
   in `raw` now. (For credentials specifically: `S ≤ CredentialTailStart(raw)`, which walks
   back over the trailing `\S+`, then `\s*[:=]?\s*`, then a keyword — an over-approximation
   of both branches of `CredentialPatternText`, which is what the existing remarks argue and
   the 300-document suite exercises.) A match extending later cannot *move*: its start is
   fixed, and its replacement text `"$1: [REDACTED]"` depends only on group 1 (the keyword),
   so growth of the greedy value does not change the replacement either.
2. **A `Replace` never touches text before its first match.** `Regex.Replace` emits
   `text[0..m₁) + repl₁ + …`, so `raw[0..C)` survives the credential transform verbatim.
   Combined with (1), no continuation can introduce a credential match below `C`, so
   `raw[0..C)` is final under that transform for the rest of the stream. By induction over
   attempts (base case `_emitted = ""`), no match ever starts below `_emitted.Length`.
3. **Each class's cap is computed on that class's own input.** `PromptGuardService.LeakagePatterns`
   lists SystemPrompt, Credential, SSN, Email; `RedactForDetectedThreats` replays detection
   order, so the full pass is credential-then-SSN-then-email — the same order the gate's
   view builds in. Credential is first, so its input is unredacted `raw`; SSN and email come
   after it, so their input is the credential-redacted view, which is what `view` holds when
   the step-5 caps run. Both caps therefore see exactly what the corresponding transform in
   the full pass will see.

Step 3 is the part that is easy to get wrong and is **load-bearing, not cosmetic**. If the
credential boundary were measured on the *view* instead of `raw`, then with PII latched
`TOKEN 123-45-6789` reads `TOKEN [SSN REDACTED]` — not a credential span, because `[` is not
a `CredentialValueChar` — so no boundary would be set, `TOKEN [SSN REDACTED]` would stream,
and the full pass (credentials first) would answer `TOKEN: [REDACTED]`. That is a divergence,
the orchestrator suppresses the tail, and the user loses the end of the answer. Measuring on
`raw` is what closes it. I have pinned that shape with five theory cases and two new
randomized fragment kinds.

Why the boundary is a *boundary* and not a post-hoc cap on `safeLength`: `unsettled` is
maintained by `ReplaceSettled` as the length of the untouched tail, an invariant across any
chain of replacements, so `view.Length - unsettled` is the exact image of the raw boundary.
Feeding a raw index in at that point keeps every downstream step in view coordinates with no
index mapping. It also removes the negative-length edge case a post-hoc cap would have had
when the first credential match sits at index 0.

Relative safety versus the old rule: the old cap was strictly *wider in start positions*
(every full-pattern match starts where the keyword pattern also matches) but strictly
*blind to step 3* (it was insensitive to view substitutions only by accident — because it
ignored the value entirely). The new rule is narrower where narrowness is provably safe by
(1)+(2), and correct where the old rule was accidentally correct. Verified empirically both
ways: the five new PII-shaped-value cases and the extended 300-document corpus pass on
master's gate too, so they are guards on my change rather than fixes for a pre-existing bug;
the five prose cases fail on master's gate and pass on mine.

### What proves it

- `StreamingGuardrailGatePropertyTests.ProseThatMerelyDiscussesCredentials_KeepsStreaming` —
  5 cases, **a latency assertion** (`final.Length − emitted.Length ≤ 40`), chosen because
  every other test in the file would pass against a gate that streamed nothing at all. Each
  case first asserts `final == document`, so it can never silently start measuring a
  redaction instead of a stall. On master's gate all 5 fail with messages like
  `344 of 346 characters never streamed; emission stopped after "A "`.
- `CredentialWhoseValueIsAlsoAPiiShape_NeverDiverges` — 5 cases pinning the ordering trap
  above (SSN-shaped and email-shaped credential values, with a leading address so PII
  latches first).
- The randomized corpus grew two new fragment kinds — keyword + SSN-shaped value and
  keyword + email-shaped value, both separator-less — plus a "prose that merely talks about
  credentials" fragment, so the 300 documents now mix the new shapes with real secrets.
  (`rng.Next(14)` → `rng.Next(17)`; every seed's document changed and every batch still
  clears its non-vacuity floor.)
- Full suite green, 6 runs, 4 at `MaxParallelThreads=32`.

---

## (b) Per-run counts

Master baseline, measured in this worktree before any edit:
**Failed 0 / Passed 1446 / Skipped 10 / Total 1456.**

| # | Command | Failed | Passed | Skipped | Total | Duration |
|---|---|---|---|---|---|---|
| 0 | `dotnet test LmKitOmniApi.Tests` (baseline, pre-change) | 0 | 1446 | 10 | 1456 | 30 s |
| 1 | `dotnet test LmKitOmniApi.Tests -- xUnit.MaxParallelThreads=32` | 0 | **1460** | 10 | 1470 | 41 s |
| 2 | `dotnet test LmKitOmniApi.Tests -- xUnit.MaxParallelThreads=32` | 0 | **1460** | 10 | 1470 | 32 s |
| 3 | `dotnet test LmKitOmniApi.Tests -- xUnit.MaxParallelThreads=32` | 0 | **1460** | 10 | 1470 | 29 s |
| 4 | `dotnet test LmKitOmniApi.Tests` (default parallelism) | 0 | **1460** | 10 | 1470 | 35 s |
| 5 | `dotnet test LmKitOmniApi.Tests` (default parallelism) | 0 | **1460** | 10 | 1470 | 34 s |
| 6 | `dotnet test LmKitOmniApi.Tests -- xUnit.MaxParallelThreads=32` (after restoring the tree post-experiments) | 0 | **1460** | 10 | 1470 | 25 s |

Run 6 was taken after the temporary revert/restore experiments; the working tree's
`git diff` md5 was verified identical (`3e85004f52a5438d2e89600337bb75f4`) before run 1 and
after run 6, so all six runs measured the same code.

1446 → 1460 is +14: 4 (`DerivedTestHostIsolationTests`) + 5
(`CredentialWhoseValueIsAlsoAPiiShape_NeverDiverges`) + 5
(`ProseThatMerelyDiscussesCredentials_KeepsStreaming`).

### Build

`dotnet build` (whole solution): **0 errors, 3 warnings, all pre-existing** —
`CS0618` (`Program.cs:39`, LM-Kit `SetLicenseKey` obsolete) and 2× `NU1903`
(`SSH.NET 2024.1.0` in `LmKitOmniApi.IntegrationTests`, a package reference I never
touched). `dotnet build LmKitOmniApi.Tests` alone: 1 warning (the `CS0618`), which is what
the pre-change baseline also emitted. **No new warnings.**

### Client

| Command | Result |
|---|---|
| `npm run lint` | **0 errors, 5 warnings** — the 5 pre-existing ones (3× `vue/no-v-html`, 1× `vue/html-closing-bracket-spacing`, 1× `vue/v-on-event-hyphenation`), none in files I touched |
| `npm run build` (`vue-tsc -b && vite build`) | clean, built |
| `npm run test:unit` | **89 passed / 8 files** (baseline 81; +8 new) |

`node_modules` was absent in the worktree; `npm ci` first. `LmKitOmniClient/components.d.ts`
is regenerated by the build with different line endings — reverted, it is not part of the
commit.

---

## (d) Left undone, and why

1. **The item-1 server change itself.** T3 owns `Application/Approvals/**`. The diff is in
   section (a), verified to compile and to translate to SQL, and the client is committed
   with the guard so it behaves exactly as today until you apply it.
2. **The item-1 client fallback (`findApprovalChatSession`).** Kept, guarded, and marked
   TRANSITIONAL with its deletion recipe. It is the "degrades to today's behaviour when the
   field is absent" you asked for. It becomes dead the moment the diff lands — the removal
   list is in section (a).
3. **No component test for `ApprovalsView.vue`.** The client has no
   `@vue/test-utils`/DOM test environment (all 8 test files are pure-module), and adding
   one is a dependency decision that is not mine to make this round. The logic that used to
   live inline in the view is now in a tested pure function, and the view is type-checked by
   `vue-tsc`.
4. **`sw.js` offline navigation fallback and per-route cache entries** (item 2 above).
   Reported, not fixed — neither is clearly wrong, and the first is a product decision.
5. **`CACHE_NAME` not bumped** (item 2 above), deliberately, with the reasoning recorded in
   the file.
6. **`ComputerUseApprovalGate` writes `IsAgentRun = true` sessions for computer-use
   approvals.** So a computer-use approval approved from the Approvals page will report
   `isChatSession: false` and land on `skipped` — which is correct (that gate's loop owns
   the continuation, not the chat endpoint), but the page's `skipped` copy only mentions
   "agent tự hành" and "đoạn chat tạm thời". Wording, not behaviour; T3's redesign may move
   it anyway, so I left the copy alone rather than churn a string T3 might delete.
