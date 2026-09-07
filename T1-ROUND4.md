# T1 · Round 4 — the tool catalog now reaches the model on every turn

**Branch** `round4/t1-toolcatalog` · base `c999f12` · LM-Kit.NET **2026.9.0** (already the repo's pin)

The defect S6 found and did not fix is fixed, and the fix is proved against two live models.
`ChatConversationFactory` now seeds **LM-Kit's own rendered tool-catalog block** at the head of a
rebuilt history, so a registered tool is advertised — and actually called — on turn 3, not only
turn 1.

---

## (a) The live before/after, verbatim

Probe: a `RotatingCodeTool` (`get_tenant_access_code`, zero-arg) that hands out a **different code
on every invocation** — `ZQ7741`, `MX9312`, `TB5580`, … Turn 1 asks for the code, turn 2 is filler,
turn 3 asks again after "the code rotated". Turn 3 therefore **cannot** be satisfied by copying
turn 1's answer out of the transcript: a fresh code can only come from a fresh call.
`BeforeToolInvocation` is the ground truth — it fires only when LM-Kit parsed a call, matched a
*registered* tool, and invoked it. History is rebuilt from the transcript on every turn, exactly as
the product does.

Probe source: `<scratchpad>/probe/Program.cs`. Raw logs: `<scratchpad>/tasks/bagk6wug8.output`
(Llama), `<scratchpad>/tasks/bz43spn4w.output` (Qwen).

### Qwen3.5-2B-Q4_K_M — `format=Qwen flags=HasReasoningSupport`

```
==================== BEFORE - persona seeded, tool catalog LOST (shipped behaviour) ====================
  trial 1
    TURN 1  catalogInPrompt=False  toolCalled=True  has ZQ7741=True
            The current tenant access code is **ZQ7741**.
    TURN 2  catalogInPrompt=False  toolCalled=False
            The capital of France is **Paris**.
    TURN 3  catalogInPrompt=False  toolCalled=False  has MX9312=False
            The current tenant access code is **K92108**.
  trial 2
    TURN 3  catalogInPrompt=False  toolCalled=False  has MX9312=False
            The current tenant access code is: **KX3921**
  trial 3
    TURN 3  catalogInPrompt=False  toolCalled=False  has MX9312=False
            The current tenant access code is **K29531**. ...
  SUMMARY BEFORE: turn1 fresh-code 3/3   turn3 fresh-code 0/3

==================== AFTER  - persona + LM-Kit-rendered tool catalog seeded ====================
  trial 1
    TURN 3  catalogInPrompt=False  toolCalled=True  has MX9312=True
            The system returned a new access code. ... The current tenant access code is **MX9312**.
  trial 2
    TURN 3  catalogInPrompt=False  toolCalled=True  has MX9312=True
            The code has rotated again ... The current tenant access code is **MX9312**.
  trial 3
    TURN 3  catalogInPrompt=False  toolCalled=True  has MX9312=False
            ... I've been calling get_tenant_access_code each time ... **RJ4408**
  SUMMARY AFTER: turn1 fresh-code 3/3   turn3 fresh-code 2/3
```

**Turn-3 tool invocation: 0/3 → 3/3.** Note what the BEFORE run actually did: with no catalog in
the prompt, Qwen did not say "I can't" — it **invented plausible-looking codes** (`K92108`,
`KX3921`, `K29531`). That is the shipped user-visible harm, not a theoretical one. Trial 3's AFTER
answer reports `RJ4408` rather than `MX9312` only because the model called the tool more than
twice across the run; it is still a genuine tool result.

(`catalogInPrompt` reads `False` throughout for Qwen because the probe's marker string
`"input_schema"` is specific to `ChatToolCallingFormat.Default`; Qwen serialises its catalog
differently. The invocation counter is the signal.)

### Llama-3.2-1B-Instruct-Q4_K_M — `format=Default flags=None`

```
==================== BEFORE - persona seeded, tool catalog LOST (shipped behaviour) ====================
    TURN 1  catalogInPrompt=True   toolCalled=True
    TURN 2  catalogInPrompt=False  toolCalled=False
    TURN 3  catalogInPrompt=False  toolCalled=False     (3/3 trials)
            "I'm back online now. The current tenant access code is: 876543210"
            "I've obtained the current tenant access code. It is... Paris."

==================== AFTER  - persona + LM-Kit-rendered tool catalog seeded ====================
    TURN 1  catalogInPrompt=True   toolCalled=True
    TURN 2  catalogInPrompt=True   toolCalled=True/False
    TURN 3  catalogInPrompt=True   toolCalled=True      (3/3 trials)
```

**Turn-3 tool invocation: 0/3 → 3/3**, and `catalogInPrompt` flips `False → True` deterministically
on turns 2 and 3. Llama-3.2-1B is too weak to reliably turn a tool result into prose (it echoes raw
`{"tool_calls":…}` JSON), which is why the content assertion is carried by Qwen.

### In the test suite

`LmKitOmniApi.Tests/ChatConversationFactoryLiveTests.cs`, opt-in on `LMKIT_LIVE_SEAM_TEST=1` plus a
reachable GGUF, skipping cleanly otherwise. Four tests, one shared model load (`LiveModelFixture`):

| test | what it proves |
|---|---|
| `NonEmptyHistory_SystemPromptReachesTheRenderedPrompt` | round 3's fix, unchanged |
| `NonEmptyHistory_ToolCatalogIsSeededOnlyWhenToolsArePassedToTheFactory` | mechanical, **no sampling**: tool name present in the scaffolding with tools, absent without |
| `RepeatedSeeding_KeepsExactlyOneCatalog` | 5 rebuilds do not stack catalogs |
| `NonEmptyHistory_ModelStillCallsARegisteredToolOnTurnThree` | end-to-end: the model calls the tool on turn 3 |

```
$ LMKIT_LIVE_SEAM_TEST=1 dotnet test LmKitOmniApi.Tests --filter "...LiveTests|...RendererTests"
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 1 m
```

---

## (b) The approach taken, and what the rejected ones measured

### What is actually broken (2026.9.0 decompile, `MultiTurnConversation.A(Message, bool, CancellationToken)`)

```csharp
bool flag = this.m_A.MessageCount == 0;
if (flag)
{
    H.D d2 = null;
    if (toolsEnabled && Tools.Count > 0)
    {
        if (!model.HasToolCalls) throw new InvalidModelException(...);
        d2 = H.D.A(model, Tools.Tools);                       // <- builds the catalog
    }
    string text = model.A().A(SystemPrompt, ReasoningLevel);
    if (d2 != null) d2.A(text, message => history.A(message)); // <- system + catalog into history
    else if (!string.IsNullOrEmpty(text)) history.A(new Message(System, text));
}
else
{
    // refreshes an internal List<string> of tool names. Renders NOTHING.
}
```

Two things follow, and the second is what made the fix possible:

1. The catalog rides the *same* `MessageCount == 0` branch as the system prompt. Since this
   codebase rebuilds the history from stored rows every turn, that branch runs once per
   conversation.
2. **Tool parsing and invocation were never on that branch.** The post-completion handler that
   parses `tool_calls`, looks the name up in `Tools`, and invokes it is gated only on
   `Tools.Count > 0` and `ToolPolicy.Choice != None`. So the catalog *text* in the prompt is the
   entire missing half — put it back and everything downstream already works.

### Chosen: seed LM-Kit's own rendered block, obtained by shape-bound reflection

`H.D` turned out to be a ~130-line formatter, not a mystery. Decompiled:

```csharp
public static D A(LM P_0, IEnumerable<ITool> P_1)          // build catalog text
public void   A(string P_0, Action<ChatHistory.Message> P_1, IList<Attachment> P_2 = null)
```

and it emits, depending on `LM.ChatTemplateFormatFlags`, either `Developer(catalog)`, or
`ToolsCatalog(catalog)` before/after the system message, or `System(prompt + "\n\n" + catalog)`.
**`AuthorRole.Developer` and `AuthorRole.ToolsCatalog` are public**, and so is
`ChatHistory.Message` — the only non-public thing in the whole path is the catalog string.

`LmKitToolCatalogRenderer` binds to that formatter **by signature, never by name**: the unique type
in the assembly with a static `(LM, IEnumerable<ITool>) -> T` factory *and* an instance
`(string, Action<ChatHistory.Message>, IList<Attachment>) -> void` emitter. It then calls LM-Kit's
own emitter and collects the messages. Consequences worth stating plainly:

* **Nothing is replicated.** Role choice, message ordering and the `"\n\n"` separator are LM-Kit's
  decisions, per model, per `ChatToolCallingFormat`. Verified byte-identical against a real turn-1
  render (both produced the same 1950-char System message for a 2-tool set).
* **Ambiguity fails the bind** (two matching types → no bind) rather than guessing.
* **Verification before use**: every registered tool name must appear in the rendered text, or the
  block is discarded.
* **Failure is closed**: no bind, or a faulting call, degrades to seeding the plain system message
  — i.e. exactly the behaviour that shipped, never something new. `LmKitToolCatalogRendererTests.
  Renderer_BindsToThisLmKitVersion` is the canary so an upgrade fails a test instead of silently
  switching function calling back off.

**Cost I introduced** (`<scratchpad>/sizeprobe`, real 6-tool `GetSafeDefaultTools()` set):

| | value |
|---|---|
| rendered block | 1 System message, **5 069 chars / 1 093 tokens** |
| CPU per turn, `BuildHistory` on a 20-message history | **0.242 ms** with tools vs 0.017 ms without (**+0.225 ms**) |
| reflective render alone | 0.040–0.079 ms/call over 500 calls |
| after 10 further rebuilds | scaffold messages 1 → 1, tokens 1270 → 1270, messages 21 (no stacking) |

The CPU cost is noise next to inference. **The honest cost is the 1 093 tokens of context, now
spent on every turn instead of only the first** — ~27 % of a 4 096-token window. That is the price
of function calling working at all; it is the same block turn 1 has always paid. No cache is
warranted at 0.2 ms, so there is none.

### Rejected, with what I measured

* **Replay the history through `AddMessage` so the `MessageCount == 0` path runs.** Impossible as
  stated: `flag` is read *inside* `Submit`, from the history's count at that instant. Anything
  replayed before `Submit` makes the count non-zero and defeats the branch; anything replayed after
  requires a completed generation first. The only public-API variant that works is a throwaway
  1-token `Submit` on a scratch conversation to harvest the rendered block — that costs a real
  inference **and** a native context allocation (`new m.B(...)` runs immediately after the render
  with no cancellation check between, so a pre-cancelled token does not avoid it). Rejected against
  0.24 ms of reflection. It remains the fallback if the bind is ever lost; it needs a loaded model,
  and for `flags=None` templates it also needs the `"\n\n"` separator derived empirically by
  harvesting twice (once with an empty prompt, once with a sentinel).
* **Re-register the catalog after construction.** No public API applies it late. `ToolRegistry`
  exposes only `Register`/`Remove`/`TryGet`/`EnsureValid`; LM-Kit's own XML doc says "Register tools
  **before** the first user turn so they are advertised to the model." Setting
  `ToolPolicy.Choice = Required/Specific` *does* apply a grammar on every turn (that path is not
  gated on `flag`), but it forces a tool call on every message — unusable for chat.
* **Render the tool descriptions into the system prompt myself.** Not needed, and would have been
  wrong: the format is per-`ChatToolCallingFormat` (the Default and Qwen catalogs differ), so a
  hand-rolled block would drift per model and per version, and a prompt advertising a call syntax
  LM-Kit cannot parse is worse than one advertising no tools. Not shipped, not even as a fallback.

---

## (c) The exact `AgentOrchestrator.cs` call-site diff

Around line 357. One argument added; one line becomes redundant.

```diff
@@ AgentOrchestrator.cs — the chat conversation
         var model = await _modelManager.GetChatModelAsync(ct: cancellationToken);
         // MUST go through the factory: assigning chat.SystemPrompt after constructing on a
         // NON-EMPTY history is silently dropped by LM-Kit (it renders the system block only
         // when MessageCount == 0, and the getter still returns what you assigned). Every turn
         // after the first was therefore generated with no persona, no project or custom
         // instructions, no memory context, and no fullContext — i.e. without this turn's own
         // ReAct/web-search result. See ChatConversationFactory.
+        //
+        // The tools go in the SAME call, not into a Register after it: LM-Kit injects the tool
+        // catalog inside that identical MessageCount == 0 branch, so a post-construction
+        // registration advertised them on turn 1 only. Create() registers them for us.
         var chat = ChatConversationFactory.Create(
-            model, history, BuildSystemPrompt(fullContext, memoryContext, options?.PersonaPrompt));
+            model, history, BuildSystemPrompt(fullContext, memoryContext, options?.PersonaPrompt),
+            _defaultToolCatalog.GetSafeDefaultTools());
         chat.MaximumCompletionTokens = DefaultMaximumCompletionTokens;
-        _defaultToolCatalog.RegisterSafeDefaults(chat);
```

Merge notes for after T3 lands:

* **The deleted line is optional.** `RegisterSafeDefaults` now registers with `overwrite: true`, so
  leaving it in place is a harmless no-op rather than the `InvalidOperationException` on a duplicate
  tool name that `Register(tool)` would have thrown. If T3's rewrite moves it, don't fight it —
  just make sure the `Create` call gained its fourth argument.
* Nothing else in `AgentOrchestrator` changes. The ReAct path at line ~596
  (`Agent.CreateBuilder(...).WithTools(...)` + `AgentExecutor`) is unaffected: each `Execute` starts
  from an empty history, so LM-Kit renders the catalog there itself.
* `GetSafeDefaultTools()` returns `IReadOnlyList<ITool>` and is already the method the ReAct path
  uses, so no new dependency is introduced.

---

## (d) Does 2026.9.0 change this?

**No — and there is nothing newer to upgrade to.**

* The brief's premise is stale: master `c999f12` **already pins 2026.9.0**
  (`LmKitOmniApi.csproj`), and 2026.9.0 is the only version left in the NuGet cache. 2026.8.6 is
  gone from it.
* Every decompile in this report is **of 2026.9.0**. The `if (history.MessageCount == 0)` gate
  around the catalog injection is present and unchanged there.
* `dotnet package search LM-Kit.NET --exact-match` lists **2026.9.0 as the newest published
  version** on nuget.org. There is no release to upgrade into that fixes this.

So this is a live upstream limitation, not an upgrade away. The right long-term ask of LM-Kit is a
public way to render, or re-apply, the tool catalog to an existing history — the internal that does
it is a pure formatter with no state, so exposing it costs them nothing.

---

## (e) What I could not fix

* **The bind is reflection into an obfuscated internal.** It is shape-based, unique-match,
  verified, and fails closed, and a unit test screams if it stops binding — but it is still not a
  supported API and a future LM-Kit can take it away. When it does, the product returns to today's
  behaviour (no catalog after turn 1), not to something broken.
* **One thing in LM-Kit's `if (flag)` branch is not reproduced.** When tools are present it may
  raise the reasoning level (`if (reasoning == None && model.<internal capability check>())
  → ReasoningLevel.Low`). The capability check has no public equivalent, so seeded turns keep
  whatever `ReasoningLevel` the caller set. Neither test model exercised it (Llama has no reasoning
  support; Qwen's `HasReasoningSupport` path called tools fine on 3/3 turn-3 runs), but on a model
  that *needs* Low to emit tool calls, turn 1 and turn 3 could still differ. Reproducing it would
  need a second reflective bind; I judged that not worth the surface until a model shows the
  symptom.
* **`!model.HasToolCalls` is still only checked on turn 1.** LM-Kit throws `InvalidModelException`
  for a tool-less model on an empty history and stays silent on a rebuilt one. Unchanged by this
  work — the first turn of any session still surfaces it.
* **Context cost is real and unmitigated**: +1 093 tokens on every turn after the first for the
  6-tool default set. If that bites a small-context deployment, the lever is trimming
  `GetSafeDefaultTools()`, not the seam.
* **I did not touch `AgentOrchestrator.cs`** (T3 owns it this round), so the fix is inert in
  production until the one-line diff in (c) is applied. `WidgetInferenceSession` registers no tools
  and is functionally unchanged; it gained a comment saying tools must go through `Create`'s
  `tools` argument if the widget ever gets any.

---

## Verification

| | result |
|---|---|
| `dotnet build` | succeeded, **0 new warnings** (the one `CS0618 LicenseManager.SetLicenseKey` warning is pre-existing in `Program.cs`) |
| `dotnet test LmKitOmniApi.Tests` | **Failed: 0, Passed: 1460, Skipped: 13** (baseline 1446 / 0 / 10) |
| `dotnet test ... -- xUnit.MaxParallelThreads=32` | **Failed: 0, Passed: 1460, Skipped: 13** |
| live, Llama-3.2-1B (`LMKIT_LIVE_SEAM_TEST=1`) | **Failed: 0, Passed: 7** |
| live, Qwen3.5-2B (`LMKIT_LIVE_SEAM_MODEL=…lmk`) | **Failed: 0, Passed: 4** — the seam works on a second, differently-templated model |

+14 passing tests (catalog-delivery rule, scaffolding-role dropping, fail-closed paths, renderer
bind canary) and +3 skipped (the new live tests, correctly skipping without the env var).

### Files changed

| file | change |
|---|---|
| `LmKitOmniApi/Infrastructure/AI/Tools/LmKitToolCatalogRenderer.cs` | **new** — the shape-bound, verified, fail-closed renderer |
| `LmKitOmniApi/Infrastructure/AI/ChatConversationFactory.cs` | `tools` on `Create`/`BuildHistory`; `PlanToolCatalog`; drops Developer/ToolsCatalog scaffolding on rebuild |
| `LmKitOmniApi/Infrastructure/AI/Tools/LmKitDefaultToolCatalog.cs` | `RegisterSafeDefaults` is idempotent (`overwrite: true`) |
| `LmKitOmniApi/Application/Widget/WidgetInferenceSession.cs` | comment only |
| `LmKitOmniApi.Tests/ChatConversationFactoryLiveTests.cs` | shared model fixture + 3 new live proofs |
| `LmKitOmniApi.Tests/ChatConversationFactoryTests.cs` | +5 weightless tests |
| `LmKitOmniApi.Tests/LmKitToolCatalogRendererTests.cs` | **new** — the bind canary |
