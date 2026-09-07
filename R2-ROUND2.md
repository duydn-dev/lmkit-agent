# R2 — rate-limit partitioning + real client IP

Branch `round2/r2-ratelimit`, based on master `79cb474`.

Files changed:

| File | What |
| --- | --- |
| `LmKitOmniApi/Infrastructure/Security/RateLimitPartitionKey.cs` | **new** — the single partition-key derivation |
| `LmKitOmniApi/Infrastructure/Security/ForwardedHeadersSetup.cs` | **new** — config-gated `UseForwardedHeaders` |
| `LmKitOmniApi/Infrastructure/Security/ApiKeyAuthenticationHandler.cs` | mints `api_key_id` |
| `LmKitOmniApi/Infrastructure/Security/DistributedAiRateLimitMiddleware.cs` | uses the shared derivation |
| `LmKitOmniApi/Program.cs` | policies use the derivation; forwarded headers registered + placed first |
| `LmKitOmniApi.Tests/RateLimitTestHost.cs` | **new** test infra |
| `LmKitOmniApi.Tests/RateLimitPartitionTests.cs` | **new** |
| `LmKitOmniApi.Tests/ForwardedHeadersTests.cs` | **new** |

No existing test file was edited. `Program.cs` diff is 33 insertions / 4 deletions, all
inside the rate-limiting and pipeline-ordering concerns.

Verification: `dotnet build` clean (only the pre-existing `LicenseManager.SetLicenseKey`
CS0618 warning). `dotnet test LmKitOmniApi.Tests` → **1116 passed / 0 failed / 9 skipped**
(master baseline 1100 / 0 / 9, plus my 16 new tests).

---

## (a) appsettings.json block for you to add

Add this top-level block — suggested position: immediately after the existing
`"RateLimiting"` block. **These values are byte-for-byte the code defaults**, so adding
the block changes nothing; it exists for discoverability.

```json
  "ForwardedHeaders": {
    "Enabled": false,
    "KnownProxies": [],
    "KnownNetworks": [],
    "ForwardLimit": 1,
    "IncludeProto": false
  },
```

Cross-checked against `ForwardedHeadersSetup.cs`:

| Key | Code default | Read at |
| --- | --- | --- |
| `Enabled` | `false` | `IsEnabled()` — `GetValue($"{SectionName}:Enabled", false)` |
| `KnownProxies` | empty (`?? []`) | `ReadList` |
| `KnownNetworks` | empty (`?? []`) | `ReadList` |
| `ForwardLimit` | `1` (`DefaultForwardLimit`) | `GetValue("ForwardLimit", DefaultForwardLimit)` |
| `IncludeProto` | `false` | `GetValue("IncludeProto", false)` |

**Do not ship `"Enabled": true`.** With `Enabled` true and both lists empty the app
**throws at startup** — that is the designed behaviour, and it means a careless
appsettings edit takes production down rather than silently trusting a spoofable header.

Nothing to change in the `"RateLimiting"` block: I did not touch any of its defaults, and
the shipped `AiRequestsPerWindow: 10` / `AiWindowSeconds: 60` already match the code.

### To actually turn it on behind nginx (not done here — I own neither file)

```json
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownProxies": [],
    "KnownNetworks": [ "172.18.0.0/16" ],
    "ForwardLimit": 1,
    "IncludeProto": false
  },
```

`KnownNetworks` must be the docker-compose bridge subnet the nginx container sits on (or
put nginx's fixed container IP in `KnownProxies`). **Until someone does this, the
per-IP limits behind nginx are still one global bucket, and the audit log still records
nginx.** The mechanism now exists and is proven; the deployment value does not exist yet.
That is deliberate — see (d).

---

## (b) What I proved, with which test

**Before/after protocol.** I ran the relevant suite on master first (1100/0/9 — matches
your baseline; note the flake in (c)). Then I reverted `Program.cs`,
`DistributedAiRateLimitMiddleware.cs` and `ApiKeyAuthenticationHandler.cs` to master
(keeping only the new `ApiKeyIdClaimType` constant so the test project compiled) and ran
the new tests against that unfixed pipeline: **6 of 16 failed**. Restoring the fix: 16/16
pass. Both numbers are from real runs, not inference.

### Failed before the fix, passes after (real proof)

| Test | What it pins down |
| --- | --- |
| `RateLimitPartitionTests.TwoApiKeysOfTheSameUser_GetIndependentBudgets` | **The headline requirement.** Two keys minted by the same user, budget of 2/hour. Key A: 400, 400, **429**. Key B's first call: **400** (was 429 — both keys shared the owner's bucket). Then key B is driven to its own 429, so the separate budget is a budget, not an exemption. |
| `RateLimitPartitionTests.JwtSessionAndApiKeyOfTheSameUser_DoNotShareOneBudget` | Cookie-JWT session exhausts its budget (400,400,429); an API key belonging to the *same user* is still served. |
| `ForwardedHeadersEnabledTests.ForwardedFor_BecomesTheClientIp_WhenThePeerIsATrustedProxy` | With the peer on the trust list, two clients behind the same proxy get independent `SharePolicy` budgets — i.e. the actual nginx bug is fixed. |
| `ForwardedHeadersStartupTests.EnablingWithoutAnyTrustedProxy_FailsAtStartup` | `Enabled: true` + no proxy/network → startup throws, message names both config keys. |
| `ForwardedHeadersStartupTests.TrustingEveryAddress_FailsAtStartup` | `KnownNetworks: ["0.0.0.0/0"]` refused. |
| `ForwardedHeadersStartupTests.AMalformedTrustList_FailsAtStartup` | A typo in the trust list is not silently skipped. |

All four rate-limit integration tests drive the **real** `Program.cs` pipeline through
`WebApplicationFactory` (auth → authorization → `UseRateLimiter` → controller), against
the real `ai-agent` policy on `POST /api/chat/stream`.

### Regression guards (pass before *and* after — they exist to fail later)

| Test | What it guards |
| --- | --- |
| `ForwardedHeadersDisabledTests.SpoofedForwardedFor_IsIgnored_WhenTheFeatureIsDisabled` | 31 requests from one peer carrying **31 different** spoofed `X-Forwarded-For` values still exhaust that peer's single bucket. Fails the moment someone enables forwarded headers unconditionally. |
| `ForwardedHeadersDisabledTests.AnonymousCallers_ArePartitionedByTheirClientIp` | The IP fallback end to end: two anonymous addresses, two `SharePolicy` budgets. |
| `ForwardedHeadersEnabledTests.ForwardedFor_IsIgnored_WhenThePeerIsNotATrustedProxy` | The known-proxy gate on an *enabled* host. Honest caveat: on the unfixed code this passed for the wrong reason (no middleware at all). Only after the fix does it actually exercise the gate. |
| 7 × `RateLimitPartitionKeyTests` | The derivation itself: api-key precedence, `user:{tenant}:{sub}`, `ip:` fallback, `anon:global`, IPv4-mapped folding, cross-branch prefix disjointness, and that `ResolveClientIp` ignores identity entirely. Supporting evidence only — these pass on master too, because the helper is new code. |

### Why "anonymous falls back to IP" is proven via `SharePolicy`, not `ai-agent`

Every `ai-agent` endpoint is `[Authorize]`, and `UseRateLimiter` runs **after**
`UseAuthorization`, so an anonymous request to one is rejected with 401 before the limiter
ever sees it. The IP branch is therefore unreachable there by construction. I proved it on
`GET /api/share/chat/{token}` (`[AllowAnonymous]` + `SharePolicy`, 30/60s) instead, which
is a real anonymous limited endpoint, plus the unit test for the branch itself.

### Test infrastructure note (this bit me, it will bite the other agents)

`TestServer` never sets `Connection.RemoteIpAddress` — it is null for every request. So
nothing about IP handling is observable by default, and worse,
`ForwardedHeadersMiddleware` deliberately accepts the first forwarded entry when the peer
is null ("allow remoteIp to be null for servers that don't support it natively"), which
would have made the known-proxy gate *look* tested while never running. I inject a fake
TCP peer via an `IStartupFilter` (`TestPeerIpStartupFilter`), which is the only hook that
runs ahead of Program.cs's first `app.Use…`.

---

## (c) Found broken, outside my ownership

1. **`LmKitApiFactory.ConfigurationOverrides` silently does nothing for settings read in
   `Program.cs`'s top-level statements.** `WebApplicationFactory` relays only *host*
   configuration to the entry point (as command-line args); `ConfigureAppConfiguration`
   values are merged during `builder.Build()`, i.e. **after** the rate-limit policies, the
   Redis/health wiring, and my forwarded-header validation have already read their
   settings. Concretely, in `ApiIntegrationTests.cs`:
   `["RateLimiting:AiRequestsPerWindow"] = "10"` and `["RateLimiting:AiWindowSeconds"] = "3600"`
   are **inert** — the AI limiter in tests actually runs on the code defaults 10/60.
   `AiRateLimit_ReturnsContractOnEleventhRequestWithoutInvokingModel` passes by
   coincidence (10 is also the default). Values read *after* `builder.Build()`
   (`HttpsRedirection:Enabled`, `Database:ApplyMigrations`, …) are fine.
   Workaround I used in my own file: `builder.UseSetting(key, value)`. Worth telling
   whoever owns test hygiene; I did not edit `ApiIntegrationTests.cs`.

2. **Full-suite flake on a cold run.** My first `dotnet test` on a clean worktree failed 5
   file-system tests — `ToolSecurityPolicyTests.FileSandbox_DoesNotAcceptSiblingWithAllowedPrefix`,
   `DocumentsControllerTests.PdfRedact_WritesOwnedFile_AndItIsDownloadable`,
   `DocumentsControllerTests.FormFill_WritesOwnedFile_AndItIsDownloadable`,
   `FilesControllerTests.Download_ResolvesWithinTheCallersOwnDirectory_SoAnotherUserGets404`,
   `FilesControllerTests.Download_ServesAnOwnedFile_WithItsContentType`. Every subsequent
   run (four of them) was 0 failed, and they pass in isolation. Looks like a first-run
   directory-creation race between parallel collections, not a real defect — but if CI
   builds cold, it will go red intermittently.

3. **The distributed (Redis) limiter has no live test at all.** Both
   `SecurityRegressionTests.RedisCircuitBreaker_*` are skipped and the test host sets
   `ConnectionStrings:Redis = ""`, so `DistributedAiRateLimitMiddleware` never executes in
   the suite. My change to it is verified by reading the code plus the shared helper's unit
   tests — **not** by an integration test. See (e).

4. **`AuditSaveChangesInterceptor.cs:43` and `AuthController.cs:39` keep recording nginx's
   address** until `ForwardedHeaders:Enabled` is turned on with a real trust list. The
   pipeline ordering is now correct for them (forwarded headers run first), but nobody has
   supplied the deployment value. R1 owns `docker-compose*.yml`; you own `appsettings.json`.

---

## (d) Judgment calls — overrule me if you disagree

1. **Fail loud on `Enabled: true` with no trusted peer.** Kept, as you asked. I agree with
   it after reading the docs: the alternative (silently keeping ASP.NET Core's loopback
   defaults) means "I enabled it and nothing happened", which is how people end up
   deleting the gate instead of configuring it.

2. **I clear the framework's default `KnownProxies`/`KnownNetworks` (IPv6 loopback).**
   This is load-bearing, not cosmetic: with the defaults left in place, "the operator
   configured nothing" is indistinguishable from "the operator configured loopback", so
   the fail-loud check in (1) could not exist. Consequence: an operator terminating on
   loopback must now list `127.0.0.1` / `::1` explicitly.

3. **I reject `0.0.0.0/0` and `::/0` in `KnownNetworks`.** This is me refusing a
   configuration an operator explicitly asked for, which is arguably over-reach. My
   reasoning: a `/0` is exactly the vulnerability the gate exists to prevent, and it is a
   plausible copy-paste. Easy to drop — delete the `PrefixLength == 0` check in
   `ForwardedHeadersSetup.ParseNetworks` and the `TrustingEveryAddress_FailsAtStartup` test.

4. **`X-Forwarded-Proto` is opt-in (`IncludeProto`, default false), not implied.** Turning
   it on rewrites `Request.Scheme`, which changes `UseHttpsRedirection`/HSTS behaviour —
   a different decision from "who is the client", and not what you asked for. Note the
   consequence: behind a TLS-terminating nginx with `HttpsRedirection:Enabled: true` you
   still get a redirect loop. That is the pre-existing state, not a regression, and the
   knob to fix it now exists. Say the word and I will flip the default.

5. **`ForwardLimit` minimum is 1; "unlimited" is not expressible.** `null` (unlimited) lets
   a client prepend its own hops.

6. **`LoginPolicy`, `SharePolicy` and `widget-auth` stay pure per-IP** — they call
   `RateLimitPartitionKey.ResolveClientIp`, which ignores identity entirely. These guard
   *unauthenticated* endpoints where the credential being brute-forced is exactly what a
   partition key would be derived from; keying on it would hand the attacker a fresh
   budget per guess. Each has a comment in `Program.cs` saying so, and
   `ResolveClientIp_IgnoresTheAuthenticatedIdentityEntirely` pins it. Two small behaviour
   changes came with the shared helper: bucket keys are now prefixed `ip:` (cosmetic,
   resets buckets on deploy) and `::ffff:1.2.3.4` now folds into `1.2.3.4` (previously two
   buckets — i.e. double budget for anyone who could pick the representation).

7. **`widget-chat` untouched**, and `UseRateLimiter` stays after `UseAuthorization`. Both
   for the reasons already in the comments there.

8. **The no-IP anonymous case is one shared `anon:global` bucket**, not a free pass. If a
   deployment ever loses `RemoteIpAddress`, unattributable callers contend with each other
   rather than each getting a full budget.

9. **The per-key claim is `api_key_id` = `TenantApiKey.Id`** (surrogate id — never the raw
   key, never the hash). It is attached to the per-request principal only, never signed
   into a token; I grepped and no endpoint enumerates `User.Claims`, so it cannot leak
   through a response.

10. **The `ai-agent` Redis key changes shape** (`user:{tenant}:{sub}` / `apikey:{id}`
    instead of a bare user id, then hashed as before). Existing in-flight buckets are
    orphaned at deploy; they expire on their own window. `BuildPartitionHash` is unchanged,
    so `SecurityRegressionTests.DistributedRateLimitPartition_IsStableAndDoesNotExposeUserId`
    still holds and I did not touch it.

---

## (e) What I did NOT verify — do not claim these

- **The Redis path.** `DistributedAiRateLimitMiddleware` now calls the same helper, so the
  two halves of the `ai-agent` budget agree by construction — but no test executes that
  middleware (no Redis in the suite). Code-reading only.
- **`IncludeProto: true`.** Zero coverage. Scheme rewriting is untested.
- **`ForwardLimit` other than 1.** The value is plumbed through and validated; no test
  drives a two-hop chain.
- **`KnownNetworks` as a *positive* trust source.** The CIDR parser and its rejections are
  tested; no test proves a peer *inside* a configured CIDR is trusted. Only `KnownProxies`
  (exact IP) is proven end to end. If you deploy with `KnownNetworks` per the snippet
  above, verify it once by hand.
- **Anything against real nginx or docker-compose.** The reverse proxy is simulated by a
  startup filter setting `Connection.RemoteIpAddress`. The forwarded-header logic itself is
  stock ASP.NET Core; what I tested is my gating of it.
- **That the audit log and the failed-login record now capture the real client.** That
  follows from middleware ordering, which I placed and reasoned about, but I assert it via
  rate-limit partitions only — I wrote no test reading `AuditLog.IpAddress`.
- **Multi-replica behaviour.** Single-process only.
