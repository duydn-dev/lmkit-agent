# Search / Qdrant / resource-lifetime fixes — integration notes

Notes for the owners of files this change deliberately did **not** touch
(`Program.cs`, `appsettings.json`, `AgentOrchestrator.cs`, `AgentMemoryService.cs`,
`LmModelManager.cs`, `ToolSandboxService.cs`). Everything below is optional
follow-up: the branch builds, and the full unit suite is green, without any of it.

---

## 1. `IWebSearchService` now returns `WebSearchOutcome`, not `string`

`LmKitOmniApi/Application/Abstractions/IAdvancedTools.cs`

```csharp
Task<WebSearchOutcome> SearchWebAsync(string query, int count = 5, CancellationToken ct = default);
```

**Why.** The old `Task<string>` sometimes returned a JSON array and sometimes
bracketed prose (`"[Web search is temporarily unavailable.]"`). Because the prose
starts with `[`, it *masqueraded* as a JSON array and blew up on the second
character — which is exactly how `Searx_LiveThroughResilientService_EmitsLegacyWireJson`
turned master red on every machine without SearXNG.

**Contract.** `WebSearchOutcome.ResultsJson` is **always** a valid JSON array
(`"[]"` when there is nothing to return, for any reason). Status/diagnostics live
in `Status` (`Success | InvalidQuery | NotConfigured | NoResults | Unavailable`)
and `Message`. `ToToolOutput()` renders the model-facing string: the raw JSON on
success, the bracketed notice otherwise — so agent-visible text is unchanged.

**No DI change is needed.** `Program.cs:563-567` keeps working as-is.
`AgentOrchestrator.cs:148/206` only forwards the service and needs no edit.

**If you add a new consumer:** parse `ResultsJson`, branch on `Status`. Never
parse `ToToolOutput()`.

---

## 2. New optional config key: `VectorStore:ApiKey`

`QdrantVectorService` used to build `new QdrantClient(uri.Host, uri.Port)`, which
ignores the URI scheme (always plaintext gRPC) and never sends credentials. It now
goes through `Infrastructure/VectorDb/QdrantClientFactory.cs`, which honors
`https://` in `VectorStore:BaseUrl` and an optional API key.

Suggested addition to `appsettings.json` (owner: whoever holds that file) — the
key is read from configuration, so `VectorStore__ApiKey` in the environment works
today without any file change:

```jsonc
"VectorStore": {
  "BaseUrl": "http://localhost:6334",
  "ApiKey": ""            // optional; sent as the Qdrant api-key header
}
```

`docker-compose.prod.yml:5` already sets `VectorStore__BaseUrl`; add
`VectorStore__ApiKey` there if the deployment's Qdrant is authenticated.

---

## 3. Qdrant payload indexes are now created on `EnsureCollectionExistsAsync`

Sparse ("hybrid") retrieval filters with `Match { Text = keyword }`, which Qdrant
**rejects** unless a full-text payload index exists on the field. Nothing in the
repo ever created one, and the failure was swallowed, so hybrid search had been
silently dense-only.

`EnsureCollectionExistsAsync` now also creates, idempotently and best-effort:

| field         | index type | why |
| ------------- | ---------- | --- |
| `Keywords`    | full text (word tokenizer, lowercase, 2..30) | required by `Match.Text` |
| `AccessScope` | keyword    | private-scope filter |
| `TenantId`    | keyword    | tenant-scope filter |
| `DocumentId`  | keyword    | pinned-knowledge allowlist |

Index creation failures are logged at Warning and never block collection
creation. **Existing collections** pick the indexes up the next time any caller
runs `EnsureCollectionExistsAsync` (RAG init, `SchemaIndexingService`,
`AgentMemoryService`, `DocumentVectorizationWorker`) — no migration needed, but
the first ensure after deploy does a little extra work while Qdrant builds the
index.

Operational note: on a very large existing collection the initial full-text index
build costs CPU/RAM on the Qdrant side. If that matters for a given environment,
pre-create the index out-of-band before deploying.

---

## 4. Behaviour changes worth knowing about

- **Sparse-search failures now propagate.** `SearchByPayloadFilterAsync` /
  `SearchByPayloadWithinDocumentsAsync` used to end in `catch (Exception) { }`.
  They now log at Warning and rethrow, which is what makes
  `RagPipelineService.PerformKeywordSearchFallbackAsync` reachable instead of
  dead code. Any *new* caller of those two methods must handle exceptions.
- **SearXNG is genuinely first** in the provider chain now (it used to land in the
  `_ => 2` catch-all, tied with the DuckDuckGo scraper and behind the paid APIs),
  matching `Program.cs:540-543`, `README.md:42` and `SearxSearchProvider`'s own
  docs.
- **Cache keys no longer collide.** The composite and the legacy DuckDuckGo layer
  hashed the same `{query}:{count}` tuple under the same `web-search:` prefix, so
  a failed scrape's cached `"[]"` was read back by the composite as its own
  successful empty result — bypassing every other provider for the full 5-minute
  TTL. Namespaces are now `web-search:composite:` and `web-search:duckduckgo:`,
  and **empty results are not cached at all**.
- **Live tests skip, never fail.** `SearchProviderTests` and
  `QdrantVectorServiceTests` probe the dependency with a 2-second TCP connect and
  `Skip.IfNot(...)`. The previous `catch (HttpRequestException) { Skip… }` guard
  could never fire, because the composite had already swallowed the exception.

---

## 5. Resource lifetimes

- `MongoDatabaseService` caches one `MongoClient` per connection string
  (`ConcurrentDictionary`, process lifetime). It used to construct and discard one
  per call from a **singleton** — a connection pool plus SDAM monitor per query.
- `QdrantVectorService` now implements `IDisposable`; as a DI singleton the
  container disposes its gRPC channel at shutdown.
- `QdrantHealthCheck` borrows `QdrantClientFactory.Shared(...)` instead of
  constructing a client in its constructor. It is not DI-registered, so
  `ActivatorUtilities` re-creates it on **every** health poll, leaking one gRPC
  channel each time. The shared client is process-lifetime and intentionally not
  disposed by the health check.

---

## 6. ⚠️ Unrelated PRE-EXISTING flake: a test mutates the process-wide cwd

**Not caused by this change, and not fixed by it** — the owning files are outside
this change's scope. But it makes `dotnet test` intermittently red for everyone,
so it needs an owner.

`LmKitOmniApi.Tests/LmModelRegistryTests.cs:15-21` changes the **process-global**
current directory in its constructor and restores it in `Dispose`:

```csharp
_previousDirectory = Directory.GetCurrentDirectory();
_modelsDirectory   = Path.Combine(Path.GetTempPath(), $"lmkit-registry-tests-{Guid.NewGuid():N}");
Directory.SetCurrentDirectory(_modelsDirectory);          // ← process-wide
```

xUnit runs test **collections in parallel**, so for the duration of that class
every other collection sees the temp directory as its cwd. Anything that resolves
paths from `Directory.GetCurrentDirectory()` then fails — notably
`ToolSandboxService`, whose allowed roots are captured in a `static readonly`
initializer (`Infrastructure/AI/Security/ToolSandboxService.cs:28-34`) and so are
pinned to whatever the cwd happened to be at type-init time.

Observed victims rotate with scheduling: `ToolSecurityPolicyTests`,
`FilesControllerTests`, `DocumentsControllerTests`, `PythonContainerExecutorTests`,
`ComputerUseExecutorTests`.

**Measured on master, with this change stashed** (5 consecutive runs): the
deterministic SearXNG failure every time, *plus* the cwd race in 2 of 5 runs
(`PythonContainerExecutorTests.ProducedFiles_AreCappedByMaxOutputFiles`,
`ComputerUseExecutorTests.Screenshot_IsHarvested_IntoOwnerUploadRoot`).
With this change applied, 5 consecutive runs: 4 green, 1 hit the same race.
So the race is strictly pre-existing; this change only removes the deterministic
failure that sat on top of it.

**Suggested fixes**, cheapest first:

1. Drop the `SetCurrentDirectory` from `LmModelRegistryTests` — all of its
   assertions are `EndsWith(...)` on a relative tail and look cwd-independent.
   Verify nothing creates stray `AIModels`/`Models` folders in the test output
   directory (`LmModelManager.cs:211-212` creates one on a *download* path that
   these tests may not reach).
2. Or put every cwd-sensitive class in one shared `[Collection("CurrentDirectory")]`
   with `LmModelRegistryTests`, so they cannot run concurrently.
3. Or make `ToolSandboxService`'s allowed roots resolve lazily (or from
   `AppContext.BaseDirectory` rather than the mutable cwd) — the more robust
   production-side fix, since a process that changes its cwd at runtime would hit
   the same bug outside tests.
