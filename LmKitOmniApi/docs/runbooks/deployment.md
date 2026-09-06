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
./scripts/build-push.sh --no-push api client   # Windows: scripts\build-push.bat --no-push api client
```

Run the isolated real-stack browser gate (uses port `18080` and a separate Compose
project/volumes; single compose file, e2e behavior is env-var driven — see the
mode-3 header comment in `docker-compose.yml`):

```bash
BOOTSTRAP_ADMIN_ENABLED=true BOOTSTRAP_ADMIN_EMAIL=e2e-admin@example.test \
BOOTSTRAP_ADMIN_PASSWORD='E2e-Admin-2026!' \
API_HOST_PORT=15032 CLIENT_HOST_PORT=18080 POSTGRES_HOST_PORT=15432 \
QDRANT_HTTP_HOST_PORT=16333 QDRANT_GRPC_HOST_PORT=16334 REDIS_HOST_PORT=16379 \
docker compose --env-file .env.example -p lmkit-fullstack-e2e up -d --wait --wait-timeout 180
cd LmKitOmniClient && npm run test:e2e:fullstack && cd ..
BOOTSTRAP_ADMIN_ENABLED=true BOOTSTRAP_ADMIN_EMAIL=e2e-admin@example.test \
BOOTSTRAP_ADMIN_PASSWORD='E2e-Admin-2026!' \
API_HOST_PORT=15032 CLIENT_HOST_PORT=18080 POSTGRES_HOST_PORT=15432 \
QDRANT_HTTP_HOST_PORT=16333 QDRANT_GRPC_HOST_PORT=16334 REDIS_HOST_PORT=16379 \
docker compose --env-file .env.example -p lmkit-fullstack-e2e down -v --remove-orphans
```

Review the generated migration before rollout:

```powershell
dotnet ef migrations script --idempotent --project .\LmKitOmniApi\LmKitOmniApi.csproj
```

## Rollout

0. Publish the images being rolled out and pin the deployment to the commit tag
   (never an unpinned `latest` in production):

   ```bash
   # Build machine (tags: latest + <git-sha>):
   ./scripts/build-push.sh            # Windows: scripts\build-push.bat
   # Target host — in .env set API_IMAGE_TAG=<git-sha> (and CLIENT_IMAGE_TAG), then:
   docker compose --env-file .env pull && docker compose --env-file .env up -d
   ```

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
2. Deploy the prior image tag (the `<git-sha>` tag published by build-push; never an unpinned `latest` in production).
3. Keep the database at the newer schema when changes are additive, as in the document/session migrations.
4. Restore the database only when data was corrupted and after preserving forensic logs.
5. Confirm health, authentication and tenant isolation before reopening traffic.

The current migrations add nullable/defaulted document lifecycle fields and indexes. Their down migration removes these fields and indexes, so running it after the new version has processed documents discards lifecycle metadata; prefer application-only rollback.

## Escalation evidence

Capture the image digest, migration ID, UTC incident window, correlation IDs, API logs, PostgreSQL/Qdrant health, affected tenant IDs and rollback decision. Never include JWTs, refresh tokens, MCP headers or raw user prompts in an incident channel.
