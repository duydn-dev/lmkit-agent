# Deployment and rollback runbook

Use this runbook for Docker/VM rollout of LM-Kit Omni Agent.

## Preconditions

- PostgreSQL backup completed and restore tested.
- TLS terminates before the browser; `AuthCookies:Secure=true`.
- JWT secret, PostgreSQL password, LM-Kit license and LiveKit credentials come from a secrets manager.
- Persistent volumes exist for `/var/lib/lmkit/keys`, `/app/Models` and `/app/Uploads`.
- Target host has enough RAM for the configured models. The default chat model is
  `bonsai` (`AiModels:DefaultChat` in `appsettings.json`); its weights must exist under
  the directory bound to `/app/AIModels`, or `/health` and `/health/ready` report
  Unhealthy — deliberately, so a deployment that cannot answer a single chat message
  never passes as ready.
- GPU hosts need `nvidia-container-toolkit`. On a CPU-only host set `API_GPU_COUNT=0`,
  otherwise the API container fails to start with "could not select device driver".
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
docker compose config --quiet                              # dev infra file
docker compose -f docker-compose.prod.yml config --quiet   # the one production file
./scripts/build-push.sh --no-push api client   # Windows: scripts\build-push.bat --no-push api client
```

Run the isolated real-stack browser gate. It uses `docker-compose.prod.yml` — the
same single file that ships production — and makes it side-by-side safe with
environment variables plus its own Compose project (`lmkit-fullstack-e2e`), so it
never touches a running stack's containers or volumes. See the mode-3 header
comment in `docker-compose.yml`.

Do not retype the environment by hand. One script owns every port, the bootstrap
admin and the compose flags, and CI runs that same script, so a local gate and
the pipeline execute identical commands:

```bash
./scripts/build-push.sh --no-push api client   # the gate runs the images, so build them first
./scripts/e2e-fullstack.sh up                  # start + wait for healthy
./scripts/e2e-fullstack.sh test                # Playwright against http://127.0.0.1:18080
./scripts/e2e-fullstack.sh down                # stop; deletes only this project's volumes
```

```powershell
scripts\build-push.bat --no-push api client
scripts\e2e-fullstack.bat up
scripts\e2e-fullstack.bat test
scripts\e2e-fullstack.bat down
```

`./scripts/e2e-fullstack.sh --help` lists the overrides (ports, admin credentials,
wait timeout, `E2E_GPU_COUNT`). The gate requests **no** GPU by default so it runs
on CPU-only machines and CI runners.

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
   docker compose -f docker-compose.prod.yml --env-file .env pull
   docker compose -f docker-compose.prod.yml --env-file .env up -d --wait
   ```

   `-f docker-compose.prod.yml` is not optional. Without it Docker loads
   `docker-compose.yml`, which is dev infrastructure only — it defines no `api` and
   no `client` service, so the rollout would quietly bring up a stack that serves
   nothing.

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
