# S4 — round 3: making the test-host configuration seam honest

Branch `round3/s4-test-seam`, based on master `140239c`.

**Verdict up front.** The seam was real and is now fixed, but the blast radius is small
and I am not going to inflate it: exactly **one** setting was overridden with a value
that differed from the shipped default and was silently ignored, and **one** test was
consequently weaker than it read. Nothing was outright wrong, and **no test newly fails**.
The second, more interesting find is structural: derived hosts
(`RateLimitTestHost.Create`) produced a host that answered the *same* configuration key
differently depending on *when* it was read — measured, not inferred.

---

## The fix

`ConfigurationOverrides` now **actually applies** rather than failing loudly, and it does
so by removing the second mechanism entirely.

- `LmKitApiFactory` and `DocumentsApiFactoryBase` no longer call
  `ConfigureAppConfiguration` at all. Every setting — the shared defaults, the
  data-protection key path, the rate-limit budget, and `ConfigurationOverrides` — is
  written as **host configuration** through `IWebHostBuilder.UseSetting`, funnelled
  through one helper (`LmKitOmniApi.Tests/TestHostConfiguration.cs`).
- Host configuration is relayed into the entry point as `--key=value` command-line
  arguments, so it is visible from the first line of `Program.cs`'s top-level statements
  **and** to anything that resolves `IConfiguration` later. One layer, one precedence
  rule (last writer wins).
- Where the seam genuinely *cannot* express something, it now throws instead of guessing:
  a `null` override value is refused (host configuration cannot express null — it would
  silently become `""`, which `GetValue<int>` throws on where an in-memory null falls back
  to the default), and a write to `ConfigurationOverrides` *after* the host has been built
  throws rather than being dropped.

### Why one path and not both

R8's draft wrote every setting to **both** layers. I measured that combination on this
solution and rejected it. Probe (temporary test, since deleted): set `Probe:Both` via
`UseSetting` *and* via `ConfigureAppConfiguration`, then read it back from the running
host:

```
HostOnly=host ;; AppOnly=app ;; Both=app ;; AiWindow=3600 (post-build) / 60 (in the limiter)
Providers=… CommandLineConfigurationProvider | JsonConfigurationProvider ×4 | … | MemoryConfigurationProvider ×3
```

The app-configuration `MemoryConfigurationProvider` is appended **after** the command-line
one, so it wins post-build. Writing to both therefore leaves a host that can answer an
early reader and a late reader differently — the same lie, relocated. It bites hardest
exactly where a derived host tries to override a value its parent already set (see the
measured `RateLimitPartitionTests` case below).

---

## (a) What `Program.cs` reads before `builder.Build()` — and therefore what was inert

`builder.Build()` is `Program.cs:707`. Every `builder.Configuration` reference in the file,
classified:

### Read EAGERLY (before `Build()`) → a `ConfigureAppConfiguration` override was INERT

| Setting | Line | Consumed by |
|---|---|---|
| `LMKit:LicenseKey` | 37 | `LicenseManager.SetLicenseKey` |
| `DataProtection:KeyPath` | 56 | data-protection key ring directory |
| `DataProtection:CertificatePath` | 62 | key-encryption certificate |
| `DataProtection:CertificatePassword` | 67 | (only when a cert path is set) |
| `PostgreSql` / `ConnectionStrings:PostgreSql` | 214, 216 | `HermesDbContext` registration + the "missing connection string" startup guard |
| `ConnectionStrings:Redis` | 427 | Redis multiplexer / distributed cache / Redis health check (583) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | 444 | OTLP metrics + tracing exporters |
| `ForwardedHeaders:Enabled` / `KnownProxies` / `KnownNetworks` / `ForwardLimit` / `IncludeProto` | 592 (`AddConfiguredForwardedHeaders`) | trusted-proxy validation — deliberately eager so a misconfiguration throws at startup |
| `RateLimiting:AiRequestsPerWindow` | 597 | `"ai-agent"` token bucket `TokenLimit`/`TokensPerPeriod` |
| `RateLimiting:AiWindowSeconds` | 598 | `"ai-agent"` `ReplenishmentPeriod` + the `Retry-After` fallback |

### Read LAZILY or after `Build()` → an override always worked

- Options-bound sections (`services.Configure<T>(section)`, read when `IOptions<T>` is
  resolved): `CodeInterpreter:Python`, `ChatReasoning`, `AgentMemory`, `BrowserTool`,
  `WebRead`, `DocumentTools`, `Lora`, `ComputerUse`, `GroundingEval`, `GroundingTraining`,
  `DatabaseAgent`, `Voice`, `ApprovalExpiry` (via `AddApprovalExpiry`, 407).
- Options callbacks: `Cors:AllowedOrigins` (78, inside `AddCors`), `JwtSettings:*`
  (98–111, inside `AddJwtBearer`).
- Post-`Build()` reads off `builder.Configuration`: `Database:ApplyMigrations` (709),
  `BootstrapAdmin:Enabled|Email|Password` (719–722), `ForwardedHeaders:Enabled` again
  (757), `HttpsRedirection:Enabled` (770).
- Per-request / per-construction reads from DI `IConfiguration`:
  `RateLimiting:WidgetAuthRequestsPerWindow|WidgetAuthWindowSeconds` (680–683),
  `RateLimiting:AiRequestsPerWindow|AiWindowSeconds` again in
  `DistributedAiRateLimitMiddleware`, `AuthCookies:Secure` and
  `JwtSettings:ExpirationInMinutes` in `AuthController`, `AiModels:*` in
  `LmModelManager` / `ModelWarmupWorker` / `LmKitModelHealthCheck`.

### Cross-referencing that against what the test hosts actually set

`LmKitApiFactory`'s 16 settings, against the eager list:

| Key it set | Was the override inert? | Did the value differ from the effective default? |
|---|---|---|
| `RateLimiting:AiWindowSeconds` = 3600 | **yes** | **yes — appsettings ships 60** |
| `RateLimiting:AiRequestsPerWindow` = 10 | yes | no — appsettings ships 10 |
| `ConnectionStrings:Redis` = "" | yes | no — appsettings ships "" |
| `JwtSettings:*`, `AuthCookies:Secure`, `HttpsRedirection:Enabled`, `Database:ApplyMigrations`, `BootstrapAdmin:Enabled`, `AiModels:WarmupChatModel`, `AiModels:RequireChatModelReady`, `RateLimiting:WidgetAuth*` | no | n/a |
| `DataProtection:KeyPath` | no — already `UseSetting` (round-2 key-ring fix) | n/a |

So: **one key was inert with a different value.** Two more were inert but happened to
restate the shipped default, which is why nothing ever went red. The `ConnectionStrings:Redis`
case is worth one line even though no test was affected: setting it to `""` was meant to
guarantee that a machine with `ConnectionStrings__Redis` in its environment does not make
the test hosts open Redis connections at startup. That guarantee did not hold (environment
variables outrank `appsettings.json` in the eager read). It holds now — command line
outranks environment.

---

## (b) Tests that were asserting against a default instead of their override

### 1. `ApiIntegrationTests.AiRateLimit_ReturnsContractOnEleventhRequestWithoutInvokingModel`

The host asked for a 3600-second AI window; the limiter ran on 60. Measured directly
through the 429's `Retry-After` header, which the token bucket derives from
`ReplenishmentPeriod`:

```
before this branch:  Retry-After=60      (appsettings.json default)
after  this branch:  Retry-After=3600    (what the test host asked for)
```

The old assertion was `int.TryParse(...) && seconds > 0` — which cannot tell 60 from 3600,
so the test passed on the default while its host claimed to have configured something else.

**What it proves now:** the eleventh request is refused without reaching the model, *and*
the refusal advertises the window the host was actually configured with. The assertion is
now `Assert.Equal(LmKitApiFactory.AiWindowSeconds, seconds)`, and the budget/window are
`const`s on the factory so the test and the host cannot drift apart. This is a
strengthening, not a weakening: the old form is a strict subset of the new one.

Consequence worth flagging (documented in the factory): with 3600 now real, the bucket does
**not** refill during a run. A `LmKitApiFactory` host's AI budget is 10 requests for the
whole test class. Nothing in the suite exceeds that today (highest is `ApiIntegrationTests`
itself at exactly 11, which is the point of the test); a future test that needs more must
build its own host. Previously the 60-second refill quietly hid this.

### 2. `RateLimitPartitionTests` / `ForwardedHeadersTests` — a two-faced host, not a wrong test

`RateLimitTestHost.Create` was already using `UseSetting` (round 2 got that right), so the
`"ai-agent"` policy really did get the requested budget of 2. But the **parent**
`LmKitApiFactory` was still layering its own defaults through `ConfigureAppConfiguration`,
which outranks the child's host configuration for anything read after `Build()`. Measured
against the pre-fix code:

```
RateLimitTestHost.Create(parent, { "RateLimiting:AiRequestsPerWindow": "7" })
  → host.Services.GetRequiredService<IConfiguration>()["RateLimiting:AiRequestsPerWindow"]
    Expected: "7"
    Actual:   "10"     ← the parent's app-configuration layer
```

So the host enforced a budget of 2 in the `"ai-agent"` policy while
`DistributedAiRateLimitMiddleware` — constructed per request from `IConfiguration`, and
described in its own comment as "two halves of ONE logical budget… they MUST agree" — read
10. No test asserted on the losing half (the middleware no-ops without a Redis multiplexer,
and the test hosts have none), so nothing was failing; the host was simply not what it said
it was. Both halves now read the same number, and
`TestHostConfigurationTests.DerivedHostOverride_IsSeenByEarlyAndLateReadersAlike` pins it.

### 3. Everything else: clean

`WidgetPublicApiTests.Exchange_IsThrottledPerClientAddress` is the only other user of
`ConfigurationOverrides`. Its keys (`RateLimiting:WidgetAuth*`) are read per request inside
the policy factory, so its override was always effective — it was proving what it claimed.

---

## (c) Newly-failing tests

**None.** All 1223 tests pass.

That is the honest result and I am not going to dress it up. The reason nothing broke is
narrow and checkable: the single inert-and-different override (`AiWindowSeconds`) only
controls how long a *drained* bucket stays drained, and no test class in the suite makes
more than ten `ai-agent` requests against one host as one principal. I looked for the
failure specifically — every `[EnableRateLimiting("ai-agent")]` route
(`ChatController.stream`, `stream-with-files`, `AgentRunsController.Start`, the whole of
`AgentsController` / `KnowledgeBaseController` / `SpeechController` / `TextAnalysisController`
/ `VisionController`, `DocumentController.convert|extract-data`, `ResearchController`,
`ComputerUseController.Run`, `GroundingEvalController.Run`) against every class holding a
`LmKitApiFactory` — and the busiest (`VoiceSpeechApiTests`, `AgentRunsApiTests`) stay under
the budget once anonymous calls (a separate partition) are excluded.

No production defect was uncovered, and I have not marked anything `Skip`.

One production observation that is **not** a defect and that I did not change (production
code is out of my scope this round): `RateLimiting:AiRequestsPerWindow|AiWindowSeconds` are
read twice — eagerly into the `"ai-agent"` policy and again by
`DistributedAiRateLimitMiddleware` at construction. In production both reads hit the same
configuration root and agree. Only a test host that mixes configuration layers can split
them, which is what item (b)(2) above was.

---

## (d) Verification — every run, individually

`dotnet build`: succeeded, **1 warning**, and it is the pre-existing
`Program.cs(39,5) CS0618 LicenseManager.SetLicenseKey is obsolete` that is already on
master. No new warnings. (The solution-wide build also shows two pre-existing `NU1903`
SSH.NET advisories from `LmKitOmniApi.IntegrationTests`, untouched by this branch.)

Master baseline: **1218 passed / 0 failed / 9 skipped**.
This branch adds 5 tests (`TestHostConfigurationTests`), so the expected total is 1223.

| # | Command | Passed | Failed | Skipped | Total | Duration |
|---|---|---|---|---|---|---|
| 1 | `dotnet test` (default parallelism) | 1223 | 0 | 9 | 1232 | 23 s |
| 2 | `dotnet test` (default parallelism) | 1223 | 0 | 9 | 1232 | 16 s |
| 3 | `dotnet test -- xUnit.MaxParallelThreads=32` | 1223 | 0 | 9 | 1232 | 16 s |
| 4 | `dotnet test -- xUnit.MaxParallelThreads=32` | 1223 | 0 | 9 | 1232 | 18 s |
| 5 | `dotnet test -- xUnit.MaxParallelThreads=32` | 1223 | 0 | 9 | 1232 | 17 s |
| 6 | `dotnet test -- xUnit.MaxParallelThreads=32` | 1223 | 0 | 9 | 1232 | 18 s |
| 7 | `dotnet test -- xUnit.MaxParallelThreads=32` | 1223 | 0 | 9 | 1232 | 16 s |
| 8 | `dotnet test` (default parallelism) | 1223 | 0 | 9 | 1232 | 21 s |
| 9 | `dotnet test -- xUnit.MaxParallelThreads=32` (post-commit re-check) | 1223 | 0 | 9 | 1232 | 20 s |

9 runs, 6 of them at `MaxParallelThreads=32`, 0 failures. The round-1/2 flake fixes
(cwd, `Uploads` tree, per-host key ring, per-host SQLite connection string) are untouched:
the per-host `DataProtection:KeyPath` and the named shared-cache database both still go
through the same host-configuration path they did before, and I changed no service
registration.

---

## (e) R8's draft — what I took and what I rejected

**Taken (as ideas, rewritten from scratch — I applied none of its hunks):**

- Merging `ConfigurationOverrides` into the same dictionary as the factory's defaults so
  "last writer wins" is a single coherent rule, rather than two `AddInMemoryCollection`
  calls whose ordering is invisible.
- The idea of a dedicated `TestHostConfigurationTests` file whose headline test is
  *behavioural* — override the AI burst to 2 and prove the third request is refused — since
  an introspective assertion can itself be fooled by a layer landing in the wrong place.
  I kept that test essentially as R8 wrote it.

**Rejected:**

- **Its core mechanism: writing every setting to both layers.** Measured above — the app
  layer wins post-build, so this preserves the divergence for derived hosts. The whole
  point was to stop a host answering two different numbers for one key. This is the main
  substantive disagreement with the draft.
- **Its second guard test as written.** `AnOverride_OutranksAppsettingsInTheRunningHost`
  asserts `configuration["RateLimiting:AiWindowSeconds"] == "3607"` — but pre-fix the
  overrides already went into the app-configuration layer, which *is* visible post-build,
  so that test would have passed against the broken code. It guards nothing on its own.
  I kept a version of it (it still pins that an override outranks the factory default) and
  added `DerivedHostOverride_IsSeenByEarlyAndLateReadersAlike`, which I verified *does*
  fail pre-fix (expected "7", actual "10").
- **Its claim that keeping the app layer makes a null-valued override "behave as before".**
  That is true and it is the problem: it silently papers over the one case the host layer
  cannot represent. I refuse nulls with an explanatory exception instead.
- **Its stale base.** R8's `ApiIntegrationTests.cs` hunk predates the round-2 key-ring and
  `SqliteConnection` fixes, and its comments cite `builder.Build()` at "line 682" and the
  burst read at "line 586" — both wrong against current master (707 and 597). Applying it
  would have reverted two flake fixes.
- **Everything else in the patch.** `ComputerUseApprovalGateLifecycleTests.cs`,
  `CredentialGuardrailCorpusTests.cs`, the `StreamingGuardrailGatePropertyTests.cs` changes
  and the four production-code hunks (`ComputerUseApprovalGate`, `OutputGuardrailFilter`,
  `PromptGuardService`, `AgentActionDispatcher`) are owned by S1/S2 or are production code —
  not mine this round, and I did not look at them beyond identifying the ownership.

---

## (f) What I did not fix

1. **`WithWebHostBuilder`-derived hosts inherit their parent's data-protection key path.**
   `RateLimitTestHost.Create(parent, …)` reuses the parent's `_dataProtectionKeyPath`, so
   parent and child would share a key ring if both were ever started. Today no caller does
   (`ForwardedHeadersTests` and `RateLimitPartitionTests` build only the derived host), so
   this is latent, not live. I left it alone rather than churn code that has been measured
   green at `MaxParallelThreads=32`. Cheap to close later: one `UseSetting` in
   `RateLimitTestHost.Create`.
2. **Five eagerly-read settings have no test coverage at all:** `LMKit:LicenseKey`,
   `PostgreSql`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `Cors:AllowedOrigins`,
   `DataProtection:CertificatePath`. They are now *overridable* from a test, but I added no
   tests for them — none of them is currently exercised and inventing coverage was outside
   this task.
3. **The shared `LmKitApiFactory` AI budget is now a hard 10 per class.** I deliberately
   kept the author's numbers (10 / 3600) rather than loosening them, so the behaviour is
   exactly what the factory says. The trap — a future eleventh `ai-agent` call in any class
   sharing the fixture will now 429 instead of waiting out a 60-second refill — is
   documented in a comment at the point of configuration, not silently smoothed over.
4. **`DocumentsApiFactoryBase` had no inert override to find.** Every key it set is either
   lazily read or identical to the shipped default, and its `DocumentToolsOptions` come from
   a code-based `services.Configure<T>(…)` that wins on registration order, not from
   configuration at all. I converted it to the single host-configuration path for
   consistency; there was no bug there to report.
