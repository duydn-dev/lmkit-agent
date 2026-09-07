# PLATFORM-FIX-INTEGRATION

Changes that must land in `LmKitOmniApi/Program.cs` / `LmKitOmniApi/appsettings.json`, which
this worktree deliberately did **not** touch. Everything else (the guard itself, the SSRF
classifier, the health check, the exception handler, the model cache, and all tests) is
already implemented and green here.

Sections:

1. `/metrics` Admin gate — **required**, this is the CRITICAL fix
2. Health-check registration — **no change needed**, plus optional diagnostics
3. Config keys — **no new keys**, but read the behaviour note
4. Widget rate-limit partition — for the other agent (separate, independent change)

---

## 1. `/metrics` Admin gate (CRITICAL — required)

### The defect, confirmed live

Probing the real pipeline (`WebApplicationFactory<Program>`, anonymous client, no
credentials) on the current `master`:

```
GET /metrics           -> 403  (0 bytes)
GET /metrics/          -> 200  (8555 bytes of Prometheus exposition)   <-- full anonymous scrape
GET /metrics/anything  -> 200  (8555 bytes of Prometheus exposition)   <-- full anonymous scrape
GET /metricsfoo        -> 404
```

`UseOpenTelemetryPrometheusScrapingEndpoint()` (no-arg overload,
`OpenTelemetry.Exporter.Prometheus.AspNetCore 1.16.0-beta.1`) registers its branch with
`IApplicationBuilder.Map("/metrics", ...)`, and `Map` matches by **prefix**
(`PathString.StartsWithSegments`). The guard matched with `Path.Equals("/metrics")` — an
exact path. Every path in the subtree except the bare one therefore skipped the guard branch
and still landed in the exporter branch.

### The change

`LmKitOmniApi/Program.cs`, lines **738–748** (the `app.UseWhen(...)` block immediately above
`app.UseOpenTelemetryPrometheusScrapingEndpoint();`).

**Replace this:**

```csharp
// Kích hoạt Prometheus Scrape Endpoint cho OpenTelemetry
app.UseWhen(
    context => context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase),
    metricsApp => metricsApp.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true || !context.User.IsInRole("Admin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }));
app.UseOpenTelemetryPrometheusScrapingEndpoint();
```

**with this:**

```csharp
// Kích hoạt Prometheus Scrape Endpoint cho OpenTelemetry.
// The guard MUST cover the same path space as the exporter: the no-arg
// UseOpenTelemetryPrometheusScrapingEndpoint() registers via Map("/metrics", ...), which
// matches by prefix (StartsWithSegments), so an exact-path guard left /metrics/ and
// /metrics/<anything> open to anonymous scrapes.
app.UseMetricsEndpointGuard();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
```

Add the using (or fully qualify the call):

```csharp
using LmKitOmniApi.Infrastructure.Security;
```

Ordering is unchanged: the guard still sits after `app.UseAuthorization();` and immediately
before the exporter, so `HttpContext.User` is populated when the role check runs.

### What it does

`LmKitOmniApi/Infrastructure/Security/MetricsEndpointGuard.cs` (new, already committed here):

- `IsMetricsRequest` → `Request.Path.StartsWithSegments("/metrics", OrdinalIgnoreCase)` —
  matches `/metrics`, `/metrics/`, `/metrics/x`; does **not** match `/metricsdashboard`.
- Non-Admin (anonymous or authenticated) → `403 Forbidden`, short-circuited.
  403 for anonymous is kept deliberately so the existing assertion in
  `ApiKeyAuthTests.AdminSurfaces_CatalogAndMetrics_AcceptBothSchemes` keeps passing.
- Admin → falls through to the exporter.

### Tests

`LmKitOmniApi.Tests/MetricsEndpointGuardTests.cs` (17 tests, passing) runs the guard in a
`TestServer` whose pipeline mirrors production — `UseAuthentication()` → guard →
`app.Map("/metrics", ...)` stand-in for the exporter (registered the same way the real one
is) — and asserts 403 + no metric bytes for anonymous and for an authenticated non-Admin
across `/metrics`, `/metrics/`, `/metrics/anything`, 200 + metric bytes for Admin on all
three, and pass-through for `/metricsdashboard`, `/api/...`, `/`.

**After you apply the Program.cs change**, add the end-to-end assertions to
`LmKitOmniApi.Tests/ApiKeyAuthTests.cs` — they cannot live in this worktree because they
would fail until the change above lands. Append inside
`AdminSurfaces_CatalogAndMetrics_AcceptBothSchemes` (the `anonymous` client is already in
scope at line 131):

```csharp
        // The exporter is registered with Map("/metrics"), i.e. by PREFIX, so the guard has
        // to cover the whole subtree — not just the exact path.
        foreach (var path in new[] { "/metrics", "/metrics/", "/metrics/anything" })
        {
            var anonymousScrape = await anonymous.GetAsync(path);
            var nonAdminScrape = await apiClient.GetAsync(path); // Admin key: allowed, asserted below
            Assert.Equal(HttpStatusCode.Forbidden, anonymousScrape.StatusCode);
            Assert.DoesNotContain(
                "target_info",
                await anonymousScrape.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, nonAdminScrape.StatusCode);
        }
```

(The seeded fixture user is an Admin, so a genuinely non-Admin end-to-end caller needs a
second seeded user; the non-Admin path is already covered by the TestServer tests above.)

---

## 2. Health-check registration (no Program.cs change required)

`LmKitModelHealthCheck` is **already registered with the `ready` tag** at
`Program.cs:575`, so `/health/ready` picks up the new behaviour with no edit:

```csharp
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<LmKitOmniApi.Infrastructure.Health.PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<LmKitOmniApi.Infrastructure.Health.QdrantHealthCheck>("qdrant", tags: ["ready"])
    .AddCheck<LmKitOmniApi.Infrastructure.Health.LmKitModelHealthCheck>("lmkit-model", tags: ["ready"]);
```

`/health` and `/health/live` are untouched: `/health/live` filters with `Predicate = _ => false`
and `/health` runs every check but is mapped separately — the model check reports on
readiness, never on liveness. Restarting the process cannot conjure a missing weights file,
so a missing model must not kill a live container.

### Behaviour change you are signing up for

`/health/ready` now answers **Unhealthy** when `AiModels:DefaultChat` names a model
registered under `AiModels:Models` whose weights file is not on disk. With the shipped
`appsettings.json` (`DefaultChat: "bonsai"` → `AIModels/bonsai/Bonsai-27B-Q1_0.gguf`, a
gitignored path with nothing committed and no download script) **that is the state of a fresh
checkout** — which is the point: the old behaviour reported healthy and failed on the first
user message instead.

Consequences to be aware of before merging:

- `docker-compose.yml:52` / `docker-compose.prod.yml:112` probe `/healthz`, not
  `/health/ready`, so container health gating is **not** affected by this change.
- Any orchestrator readiness probe pointed at `/health/ready` will now hold traffic off an
  instance that has no chat model. That is the intended fix, but it means the model files
  have to be present (or `AiModels:DefaultChat` repointed) before such a deployment goes
  ready.
- A model id that is **not** in the local registry (an LM-Kit catalog id such as
  `qwen3.5:2b`, or an `https://` URL) is not reported as missing — those can only be
  resolved by attempting a load, so they stay healthy rather than raising a false alarm.

### Optional: surface the reason over HTTP

The default health response writer emits only the status word (`Unhealthy`), so the
actionable description is visible in logs but not on the wire. **`/health/ready` is
unauthenticated and the description contains absolute filesystem paths**, so publishing it
is an information leak in production. If you want it in Development only:

```csharp
var readyOptions = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
};

if (app.Environment.IsDevelopment())
{
    readyOptions.ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description
            })
        });
    };
}

app.MapHealthChecks("/health/ready", readyOptions);
```

Recommendation: **skip this** and read the reason from the startup logs instead (below).
Nothing in this worktree depends on it.

### Startup logging (already wired, no registration needed)

`LmModelManager`'s constructor now audits every configured default against the registry.
`LmModelManager` is a singleton and `ModelWarmupWorker` takes it as a constructor
dependency, so the audit runs at host start even with `WarmupChatModel=false`. A missing
chat model logs at **Critical**, other roles at **Error**:

```
crit: LmKitOmniApi.Services.LmModelManager[0]
      Model 'bonsai' (configured by AiModels:DefaultChat) has no weights file at
      '/app/AIModels/bonsai/Bonsai-27B-Q1_0.gguf'. Place the file there, or point
      AiModels:Models:bonsai:Path / AiModels:DefaultChat at a model that exists.
```

An actual failed load additionally logs Critical with the exception, and the failure message
is kept on `LmModelManager.LastChatModelLoadError` (previously only the exception type name)
so `/health/ready` can report *why*.

---

## 3. Config keys

**No new configuration keys.** Existing semantics, sharpened:

| Key | Before | Now |
| --- | --- | --- |
| `AiModels:RequireChatModelReady` | `false` short-circuited the whole check to Healthy | still governs whether the model must already be **loaded** (a warmup concern). It no longer suppresses the *resolvability* check — a chat model that cannot be resolved is unhealthy either way. |
| `AiModels:WarmupChatModel` | unchanged | unchanged. Startup still does not block on a model load; the audit is a filesystem check, not a load. |
| `AiModels:DefaultChat`, `AiModels:Models:<key>:Path`, `:Mmproj` | unchanged | now named verbatim in the unhealthy description and the startup log. |

### Two `appsettings.json` observations (reported, NOT changed — model choice is yours)

Both are noted for the record; neither is fixed here, because picking a model is a product
decision:

- `AiModels:Models:bonsai:Path = "bonsai/Bonsai-27B-Q1_0.gguf"` — `Q1_0` is not a valid GGUF
  quantization tag (llama.cpp ships `Q2_K`, `IQ1_S`, `IQ1_M`, …; there is no `Q1_0`), so this
  filename most likely never existed.
- `AiModels:DefaultEmbedding` and `AiModels:DefaultReranker` both resolve to `bge-m3`. The
  **duplicate load is now fixed in code** — `LmModelManager` keys loaded models by resolved
  file path and hands both roles the same `LM` instance, so the weights are materialized
  once. Nothing to change in config; if you later point the two roles at different files
  they simply stop sharing.

---

## 4. Widget rate-limit partition — move `UseRateLimiter()` after `UseAuthorization()`

For the other agent. Independent of everything above; listed here because it is another
`Program.cs` ordering edit.

### The defect

`Program.cs:644-658` partitions the `widget-chat` policy on two values:

```csharp
options.AddPolicy("widget-chat", httpContext =>
{
    var tenant = httpContext.User.FindFirst("TenantId")?.Value ?? "unknown";
    httpContext.Items.TryGetValue("Widget.RequestOrigin", out var originObj);
    var origin = originObj as string ?? "unknown";
    return RateLimitPartition.GetTokenBucketLimiter($"widget:{tenant}:{origin}", ...);
});
```

Neither value exists when the limiter runs, because `app.UseRateLimiter()` is at
`Program.cs:734` — **before** `app.UseAuthorization()` at line 735:

- `WidgetTokenAuthenticationHandler` is registered as a **non-default** scheme
  (`Program.cs:181-182`), and the `WidgetOrigin` policy names it explicitly via
  `AddAuthenticationSchemes` (`Program.cs:199`). A non-default scheme is authenticated by
  **`AuthorizationMiddleware`**, not by `UseAuthentication()`. So at limiter time
  `httpContext.User` carries no `TenantId` claim → `tenant = "unknown"`.
- `Items["Widget.RequestOrigin"]` is written by
  `Infrastructure/Security/WidgetOriginRequirement.cs:84`, inside the authorization handler
  → `origin = "unknown"`.

Every widget request from every tenant and every origin therefore shares the single partition
`widget:unknown:unknown` — one 20-token/60s bucket for the whole deployment. One busy tenant
starves all the others, and the per-origin limit does not exist.

### The change

`LmKitOmniApi/Program.cs`, lines **729–735**.

**Replace this:**

```csharp
app.UseRouting();
app.UseAuthentication();
// Authentication must run first so rate-limit partitions use the stable user id
// instead of grouping every signed-in caller behind the same proxy IP.
app.UseMiddleware<LmKitOmniApi.Infrastructure.Security.DistributedAiRateLimitMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
```

**with this:**

```csharp
app.UseRouting();
app.UseAuthentication();
// Authentication must run first so rate-limit partitions use the stable user id
// instead of grouping every signed-in caller behind the same proxy IP.
app.UseMiddleware<LmKitOmniApi.Infrastructure.Security.DistributedAiRateLimitMiddleware>();
app.UseAuthorization();
// AFTER UseAuthorization: the widget-chat partition keys on the TenantId claim and on
// Items["Widget.RequestOrigin"]. The widget token is a NON-DEFAULT auth scheme named by the
// "WidgetOrigin" policy, so its principal — and the origin item, written by
// WidgetOriginRequirement — are both produced by AuthorizationMiddleware, not by
// UseAuthentication. Running the limiter first collapsed every tenant and origin into the
// single partition "widget:unknown:unknown".
app.UseRateLimiter();
```

Only `UseRateLimiter()` moves — one line down, past `UseAuthorization()`.
`DistributedAiRateLimitMiddleware` stays where it is: it partitions on the **default**
scheme's user id, which `UseAuthentication()` has already established.

### Trade-off to accept knowingly

Rate limiting now runs after authorization, so a rejected request is authorized before it is
throttled. `LoginPolicy` (`Program.cs:618`, 5 req/10s per IP) and `SharePolicy`
(`Program.cs:631`, 30 req/60s per IP) both guard `[AllowAnonymous]` endpoints, where
authorization is a cheap pass-through, and both partition by remote IP rather than by
principal — so their protection is unchanged in substance. If cheap pre-auth shedding on the
login endpoint matters, keep a second `UseRateLimiter()`-independent IP gate rather than
moving this one back.

### Suggested test

A widget-chat request from tenant A and one from tenant B must land in **different**
partitions. The end-to-end shape is already available:
`LmKitOmniApi.Tests/WidgetPublicApiTests.cs` drives the real widget auth/policy/quota
pipeline with a canned chat engine, so exhausting tenant A's 20-token bucket and then
asserting tenant B still gets `200` is the direct regression test. Before the move it returns
`429`.

---

## Files changed in this worktree (for reference — no action needed)

| File | Change |
| --- | --- |
| `LmKitOmniApi/Infrastructure/Security/MetricsEndpointGuard.cs` | **new** — subtree-covering Admin gate, `UseMetricsEndpointGuard()` |
| `LmKitOmniApi/Infrastructure/Security/PrivateNetworkClassifier.cs` | **new** — the single authoritative SSRF address classifier |
| `LmKitOmniApi/Infrastructure/AI/Security/ToolSandboxService.cs` | `IsPrivateOrLocalAddress` now forwards to the classifier |
| `LmKitOmniApi/Services/LmModelManager.cs` | forwards to the classifier; load-once shared model cache; startup availability audit; richer `LastChatModelLoadError` |
| `LmKitOmniApi/Infrastructure/Security/SsrfSafeConnect.cs` | calls the classifier directly |
| `LmKitOmniApi/Infrastructure/AI/Security/DbEgressValidator.cs` | calls the classifier directly |
| `LmKitOmniApi/Infrastructure/Health/LmKitModelHealthCheck.cs` | unresolvable chat model ⇒ unhealthy, with an actionable description |
| `LmKitOmniApi/Infrastructure/Exceptions/GlobalExceptionHandler.cs` | server faults ⇒ 5xx, `HasStarted` guard, client-cancel is not an error |
