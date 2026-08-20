# ADR-001: LM-Kit.NET-native AI runtime

**Status:** Implemented in code; external-service validation remains
**Date:** 2026-08-12
**Scope:** Chat orchestration, tools, multi-agent, memory, RAG, MCP, safety, audit and deployment

## Decision

The application uses LM-Kit.NET native `Agent` instances, `PlanningStrategy.ReAct`,
structured `ITool` schemas and `SupervisorOrchestrator`. Application policy remains the
mandatory execution boundary because it owns tenant/user authorization, file ownership,
approval, rate limiting, output limits and audit persistence.

The old string-action planner, keyword model router, generic MCP selector, custom DAG and
unused Neo4j runtime have been removed from active code. Generated Office/Image/PDF
`LMFunction` surfaces containing placeholders are excluded from compilation until they are
implemented and reviewed.

## Default LM-Kit.NET tools

Only deterministic, side-effect-free built-ins are globally registered:

| Built-in | Purpose | Default policy |
|---|---|---|
| `BuiltInTools.CalcArithmetic` | Exact arithmetic | Enabled |
| `BuiltInTools.DateTimeNow` | Current date/time | Enabled |
| `BuiltInTools.JsonParse` | Parse prompt-provided JSON | Enabled |
| `BuiltInTools.CsvParse` | Parse prompt-provided CSV | Enabled |
| `BuiltInTools.XmlParse` | Parse prompt-provided XML | Enabled |
| `BuiltInTools.StatsAnalysis` | Descriptive statistics | Enabled |

File, document, image, audio, web and MCP functions are application tools. They are exposed
only through the smallest matching profile (`SafeChat`, `Research`, `ImageRead`, `AudioRead`,
`ExternalMcp`) and rechecked at execution time.

The following remain disabled by default: arbitrary filesystem access, arbitrary URL fetch,
environment/secret inspection, process/shell execution, unrestricted database mutation,
delete operations and any generated placeholder tool.

## Runtime flow

1. Authenticate and derive `{tenantId, userId, role, sessionId}` from trusted state.
2. Sanitize input and apply prompt-injection checks and input budgets.
3. Recall only user-owned plus explicitly shared memory; keep session history separate.
4. Resolve the minimum tool profile.
5. Run an LM-Kit native ReAct agent with bounded iterations and completion tokens.
6. Route application and specialist tool calls through permission, sandbox, resilience and audit.
7. Use LM-Kit supervisor orchestration for specialist delegation and synthesis.
8. Buffer final output, redact credentials/PII, then emit it over SSE.
9. Persist filtered output and confirmed memory; update/delete the matching Qdrant vector.

## Implemented controls

### Identity and tenant isolation

- All AI/document/memory endpoints require authorization.
- Session lookup requires session, tenant and user ownership.
- Request DTO tenant IDs are ignored in favor of claims/database state.
- JWT role mapping is explicit; refresh tokens are random, hashed at rest and rotated.
- Upload paths are server-generated under `Uploads/{tenant}/{user}` with size/type limits.
- LiveKit participant identity and room names are derived from authenticated identity.

### Tools, MCP and approval

- Each MCP definition becomes one strict JSON-schema LM-Kit tool; no fuzzy generic MCP call.
- MCP endpoints are checked against URL scheme, DNS results, loopback/link-local/private IPs.
- Read tools are role-allowlisted; mutation verbs require human approval.
- Approval claiming is atomic (`Pending -> Executing`) so repeated requests cannot execute twice.
- Approval payloads are encrypted with persisted ASP.NET Data Protection keys.
- Tool audit records contain tenant, call ID, arguments SHA-256, duration, status and approval ID;
  raw arguments and secrets are never written to audit details.
- In-memory tool rate limits are singleton, thread-safe and scoped by tenant/user/tool.

### Multi-agent and ReAct

- Primary agent uses native ReAct with a maximum of five iterations.
- Specialist workers use native ReAct and a common tool gateway.
- `SupervisorOrchestrator` delegates and synthesizes bounded worker results.
- Content creation fact checking uses guarded RAG/web tools and requires evidence/uncertainty.
- Chat inference concurrency is bounded independently from single-flight model loading.

### Memory and RAG

- Memory recall always filters after cache load and filters inside Qdrant before top-K.
- Query embeddings are computed once; stored memory vectors are searched rather than re-embedding rows.
- Retention policy, contradiction overwrite, confirmation state, forget API and vector deletion exist.
- Session context stays in the owned chat session; durable facts stay user-scoped by default.
- RAG dense and sparse retrieval apply tenant filters in Qdrant and return source labels.
- Neo4j was removed from the runtime because it was not connected to ingestion/query.

### Operations and deployment

- OpenTelemetry metrics/traces support optional OTLP export; `/metrics` requires Admin.
- Telemetry stores lengths/types, not query or response previews.
- PostgreSQL and Qdrant readiness checks replace the previous always-healthy endpoint.
- Audit interceptor redacts credentials, keys, tokens, PII and conversational content.
- The simulated fine-tuning and hard-coded reflexion jobs were removed. The proactive monitor remains disabled unless explicitly enabled.
- Windows CUDA is referenced only on Windows; Linux containers use LM-Kit base CPU/Vulkan.
- Frontend uses same-origin nginx proxying, credentialed API calls and lazy-loaded voice code.

## Verification gates

Completed locally:

- Unit/regression tests for schemas, permission/rate policy, memory scope/retention,
  prompt/output guards, controller authorization, payload encryption and concurrency leases.
- Backend Debug/Release build and test suite.
- Frontend type-check and production build.
- NuGet and npm vulnerability audit.
- Compose configuration validation and EF migration discovery.

Environment-dependent gates before production rollout:

- Apply `20260812000100_AddAuditTenantId` to a disposable PostgreSQL instance, then production.
- Run authenticated HTTP integration tests against PostgreSQL, Qdrant, Redis and configured MCP servers.
- Run a golden model evaluation for tool selection, groundedness and memory precision using the
  actual licensed model artifact; target >=95% correct tool selection and zero cross-user recall.
- Run cancellation/backpressure/load tests on target CPU/GPU hardware.
- Build and smoke-test Docker images with Docker Desktop/daemon running.
- Configure a real OTLP collector and production LiveKit deployment/TLS/TURN if voice is enabled.

No mutating tool should be enabled outside the approval path until its rollback semantics have
an environment-level integration test.
