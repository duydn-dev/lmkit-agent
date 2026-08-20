# Deployment and rollback runbook

Use this runbook for Docker/VM rollout of LM-Kit Omni Agent.

## Preconditions

- PostgreSQL backup completed and restore tested.
- TLS terminates before the browser; `AuthCookies:Secure=true`.
- JWT secret, PostgreSQL password, LM-Kit license and LiveKit credentials come from a secrets manager.
- Persistent volumes exist for `/var/lib/lmkit/keys`, `/app/Models` and `/app/Uploads`.
- Target host has enough RAM for the configured models; the default chat model is `qwen3.5:2b`.
- Production sets `LMKIT_REQUIRE_LICENSE=true`, `AI_WARMUP_CHAT_MODEL=true` and
  `AI_REQUIRE_CHAT_MODEL_READY=true`. This makes `/health/ready` fail until the
  license is configured and the chat model has loaded successfully.

## Pre-deployment gates

```powershell
dotnet test .\LmKitOmniApi.Tests\LmKitOmniApi.Tests.csproj -c Release
dotnet build .\LmKitOmniApi\LmKitOmniApi.csproj -c Release
Set-Location .\LmKitOmniClient
npm ci
npm audit --audit-level=high
npm run test:unit
npx playwright install chromium
npm run test:e2e
Set-Location ..
docker compose config --quiet
docker compose build api client
```

Review the generated migration before rollout:

```powershell
dotnet ef migrations script --idempotent --project .\LmKitOmniApi\LmKitOmniApi.csproj
```

## Rollout

1. Deploy one API instance with `Database:ApplyMigrations=true`.
2. Wait for `/health/ready` to return HTTP 200. With production model gates enabled,
   this proves PostgreSQL, Qdrant, Redis, the LM-Kit license and chat model readiness.
3. Verify login → `/api/auth/me` → refresh → logout; the old token must return 401.
4. Verify a tenant admin cannot list or update a user from another tenant.
5. Upload, list and delete a small document. The list response must not contain `FilePath`.
6. Scale the API only after the migration and smoke checks pass.
7. Send the configured number of AI requests from one smoke identity and verify the
   next request returns 429 with `Retry-After`; confirm a `rate:ai:*` key exists in Redis.
8. Monitor HTTP 5xx/429, model-load failures, document `Failed` status, Qdrant latency and PostgreSQL saturation.

## Rollback triggers

Rollback immediately if any of these occur:

- authentication or token revocation fails;
- tenant-crossing access is observed;
- migrations fail or API health remains non-200 for five minutes;
- model loading causes sustained memory pressure above 90%;
- document workers create duplicate chunks or persistent failed leases;
- error rate exceeds 2% for five minutes.

## Application rollback

1. Stop new traffic to the failed version.
2. Deploy the prior image tag; never use an unpinned `latest` tag in production.
3. Keep the database at the newer schema when changes are additive, as in the document/session migrations.
4. Restore the database only when data was corrupted and after preserving forensic logs.
5. Confirm health, authentication and tenant isolation before reopening traffic.

The current migrations add nullable/defaulted document lifecycle fields and indexes. Their down migration removes these fields and indexes, so running it after the new version has processed documents discards lifecycle metadata; prefer application-only rollback.

## Escalation evidence

Capture the image digest, migration ID, UTC incident window, correlation IDs, API logs, PostgreSQL/Qdrant health, affected tenant IDs and rollback decision. Never include JWTs, refresh tokens, MCP headers or raw user prompts in an incident channel.
