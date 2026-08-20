# LM-Kit Omni Agent

Local-first, multi-tenant AI agent platform built with ASP.NET Core, Vue and LM-Kit.NET. The runtime includes native ReAct agents, supervisor-based multi-agent delegation, user-scoped memory, RAG, HITL approvals, structured default tools, document processing, vision, speech and MCP server integration.

## Quick start

Prerequisites: Docker Desktop with at least 12 GB available memory.

```powershell
Copy-Item .env.example .env
# Edit .env and replace POSTGRES_PASSWORD and JWT_SECRET_KEY.
docker compose up --build -d
docker compose ps
```

The API applies pending EF migrations before accepting traffic. Open `http://localhost`. No default administrator is created. For an empty local database, temporarily configure `BootstrapAdmin__Enabled=true`, `BootstrapAdmin__Email` and `BootstrapAdmin__Password`, start the API once, then disable those settings.

The bundled Compose stack serves plain HTTP for local development, so `AUTH_COOKIE_SECURE=false`. Any internet-facing deployment must terminate TLS and set `AUTH_COOKIE_SECURE=true`.

## Architecture

```text
Vue/Nginx ──cookie JWT──> ASP.NET Core API
                              │
            ┌─────────────────┼───────────────────┐
            │                 │                   │
       PostgreSQL          Qdrant              Redis
    users/chat/audit    RAG + memory      cache/revocation
                              │
                        LM-Kit.NET models
                 ReAct / supervisor / vision / speech
```

AI safety boundaries:

- The server chooses model IDs; chat callers cannot supply model URLs.
- Remote configured models are HTTPS-only, host/DNS checked, size- and timeout-limited, and downloaded atomically.
- RAG vectors use a private `tenant + user` access scope by default.
- Tools pass permission, sandbox, timeout, output-budget, audit and HITL controls.
- Model output is escaped before frontend HTML rendering.
- AI endpoints use per-user token-bucket rate limiting.

Safe LM-Kit default tools enabled for ReAct are arithmetic, date/time, JSON, CSV, XML and statistics. File-changing tools are not exposed as defaults.

## Development

```powershell
dotnet test .\LmKitOmniApi.Tests\LmKitOmniApi.Tests.csproj -c Release
dotnet build .\LmKitOmniApi\LmKitOmniApi.csproj -c Release

Set-Location .\LmKitOmniClient
npm ci
npm run build
```

Important configuration:

| Setting | Purpose |
|---|---|
| `JwtSettings__SecretKey` | JWT signing secret, at least 32 bytes |
| `AuthCookies__Secure` | Must be `true` behind production HTTPS |
| `Database__ApplyMigrations` | Apply pending migrations on API startup |
| `DataProtection__KeyPath` | Persistent key ring for encrypted approvals/MCP headers |
| `DataProtection__CertificatePath` | Optional PKCS#12 certificate used to encrypt the key ring at rest |
| `AiModels__DefaultChat` | Server-controlled LM-Kit model ID |
| `SemaphoreLimits__Chat` | Concurrent chat inference limit |
| `LMKit__LicenseKey` | Optional LM-Kit commercial license |

Operational rollout and rollback procedures are in [the deployment runbook](LmKitOmniApi/docs/runbooks/deployment.md). The current functional assessment is in [the audit report](LmKitOmniApi/docs/audits/2026-08-20-functional-assessment.md).

## Current boundaries

- The `/widget/chat` route requires an authenticated application session until a dedicated origin-bound widget credential flow is implemented.
- MCP configuration is tenant-admin scoped and secrets are encrypted, while transport currently targets the project's REST MCP adapter (`/mcp/tools`, `/mcp/invoke`).
- Real model-quality gates require licensed/configured model artifacts and are separate from deterministic unit and API smoke tests.
