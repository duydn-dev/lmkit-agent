# WIDGET-FIX-INTEGRATION.md

Changes the widget fix needs in files it does **not** own (`LmKitOmniApi/Program.cs`,
`LmKitOmniApi/appsettings*.json`). Apply these exactly; nothing else is required.

**Only item 1 is mandatory.** `POST /api/widget/auth` now carries
`[EnableRateLimiting("widget-auth")]`, and ASP.NET throws at request time when a
referenced policy is not registered — so without item 1 the widget key exchange
returns a 500 in production. The test host registers the same policy temporarily
(see item 3) so the suite stays green until this lands.

---

## 1. REQUIRED — register the `widget-auth` rate-limit policy

`LmKitOmniApi/Program.cs`, inside `builder.Services.AddRateLimiter(options => { … })`.
Add next to the existing `SharePolicy` / `widget-chat` policies (order relative to
them does not matter):

```csharp
    // Anonymous widget key exchange (POST /api/widget/auth): per-IP fixed window,
    // same shape as LoginPolicy/SharePolicy. It is unauthenticated and drives a
    // key-hash lookup, so it must be throttled before it reaches the database.
    options.AddPolicy(LmKitOmniApi.Controllers.WidgetPublicController.AuthRateLimitPolicyName, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue("RateLimiting:WidgetAuthRequestsPerWindow", 30),
                Window = TimeSpan.FromSeconds(httpContext.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue("RateLimiting:WidgetAuthWindowSeconds", 60)),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
```

`WidgetPublicController.AuthRateLimitPolicyName` is the constant `"widget-auth"`;
using the constant keeps the attribute and the registration from drifting apart.

The two configuration keys are optional — the defaults (30 requests / 60 seconds)
apply when they are absent, so **no `appsettings.json` change is needed**. Add them
only to re-tune:

```jsonc
"RateLimiting": {
  "WidgetAuthRequestsPerWindow": 30,
  "WidgetAuthWindowSeconds": 60
}
```

### Note for the agent fixing the widget rate-limit partition ordering

`widget-chat` partitions on `Widget.RequestOrigin` from `HttpContext.Items`, which
is only populated after the `WidgetOrigin` authorization policy has run — that is
the ordering bug being fixed separately. `widget-auth` above deliberately does
**not** depend on `HttpContext.Items`: it partitions on the remote address only, so
it is safe wherever it is placed.

Both per-IP policies (this one, `LoginPolicy`, `SharePolicy`) currently partition on
`Connection.RemoteIpAddress`, and the API has no `UseForwardedHeaders`, so behind
nginx every caller shares the reverse proxy's address and these limits act globally
rather than per client. Worth fixing while that partition work is open (add
`app.UseForwardedHeaders(...)` with a trusted-proxy configuration and the partitions
become per real client) — but it is pre-existing and out of scope here.

---

## 2. Optional — nothing else to register

No new DI registrations are required.

* The engine's new LM seam (`IWidgetInferenceSessionFactory`) is created by
  `WidgetChatEngine`'s production constructor, so the existing
  `AddScoped<IWidgetChatEngine, WidgetChatEngine>()` at `Program.cs:209` keeps
  working unchanged.
* `WidgetQuotaService` (`Program.cs:208`) now takes
  `(ILogger<WidgetQuotaService>, IConnectionMultiplexer? redis = null)`. Both are
  satisfied by the existing container: `IConnectionMultiplexer` is registered only
  when `ConnectionStrings:Redis` is set, and the optional parameter covers the
  Redis-less case — the same pattern `ToolPermissionService` and
  `AgentResiliencePolicy` already use.

---

## 3. After item 1 is applied — delete the temporary test-host registration

`LmKitOmniApi.Tests/ApiIntegrationTests.cs`, in `LmKitApiFactory.ConfigureWebHost`,
contains a block commented `TEMPORARY:` that registers the same `widget-auth`
policy. It is wrapped in `try/catch (ArgumentException)` because
`RateLimiterOptions.AddPolicy` throws on a duplicate name, so it silently no-ops
once `Program.cs` owns the policy. It can be deleted at your convenience; leaving it
in place is harmless.

Keep the two `RateLimiting:WidgetAuth*` entries in that factory's in-memory
configuration — they widen the window so the widget suite's key exchanges never trip
the limiter, and `Exchange_IsThrottledPerClientAddress` overrides them on its own
host to prove the throttle works.

---

## 4. Deployment note — nginx must be reloaded with the new config

`LmKitOmniClient/nginx.conf` gained a `map` block (http context, above `server`) and
two locations: `= /widget/chat` (per-tenant `frame-ancestors`, sourced from the API
via `auth_request`) and the internal `= /internal/widget-frame-policy` subrequest.
The client image ships `nginx:alpine`, which includes `ngx_http_auth_request_module`,
so no image change is needed — but the client container must be rebuilt/redeployed
for the widget to be embeddable at all.
