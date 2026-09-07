#!/usr/bin/env bash
# =============================================================================
# Isolated full-stack browser gate for LM-Kit Omni Agent (Linux / macOS / WSL).
# Windows equivalent: scripts/e2e-fullstack.bat
#
# ONE compose file. There is no e2e compose file, no override file and no second
# stack behind a profile: this runs docker-compose.prod.yml — the same file that
# ships production — and makes it side-by-side safe purely with environment
# variables plus a separate Compose project name. See the mode-3 header comment
# in docker-compose.yml.
#
# THIS SCRIPT IS THE ONLY PLACE THE E2E ENVIRONMENT IS DEFINED. CI calls it too
# (.github/workflows/ci.yml, job `fullstack-e2e`), so a developer here and a run
# on the runner execute byte-identical commands. That is the point: the previous
# copy-pasted env wall lived in three files and had already drifted — one copy
# was missing `-f docker-compose.prod.yml` entirely and therefore booted the
# dev-infra file, which has no api and no client service.
#
# Usage:
#   ./scripts/e2e-fullstack.sh up            # build nothing, start the stack, wait for healthy
#   ./scripts/e2e-fullstack.sh test          # run the Playwright fullstack suite against it
#   ./scripts/e2e-fullstack.sh down          # stop it and delete ITS volumes only
#   ./scripts/e2e-fullstack.sh ci            # up + test + down (down always runs)
#   ./scripts/e2e-fullstack.sh --help
#
# Typical local run (images must exist first):
#   ./scripts/build-push.sh --no-push api client
#   ./scripts/e2e-fullstack.sh up && ./scripts/e2e-fullstack.sh test
#   ./scripts/e2e-fullstack.sh down
#
# Environment overrides (all optional — defaults below are the CI values):
#   E2E_PROJECT              Compose project name        (default: lmkit-fullstack-e2e)
#   E2E_CLIENT_PORT          host port for Nginx/client  (default: 18080)
#   E2E_API_PORT             host port for the API       (default: 15032)
#   E2E_POSTGRES_PORT / E2E_QDRANT_HTTP_PORT / E2E_QDRANT_GRPC_PORT / E2E_REDIS_PORT
#   E2E_ADMIN_EMAIL          bootstrap admin login       (default: e2e-admin@example.test)
#   E2E_ADMIN_PASSWORD       bootstrap admin password    (default: E2e-Admin-2026!)
#   E2E_WAIT_TIMEOUT         seconds for `up --wait`     (default: 180)
#   E2E_GPU_COUNT            GPUs for the API container  (default: 0 = CPU only)
# =============================================================================
set -euo pipefail

cd "$(dirname "$0")/.."

# ── The single source of truth for e2e configuration ────────────────────────
PROJECT="${E2E_PROJECT:-lmkit-fullstack-e2e}"
COMPOSE_FILE="docker-compose.prod.yml"
ENV_FILE="${E2E_ENV_FILE:-.env.example}"

CLIENT_PORT="${E2E_CLIENT_PORT:-18080}"
API_PORT="${E2E_API_PORT:-15032}"
POSTGRES_PORT="${E2E_POSTGRES_PORT:-15432}"
QDRANT_HTTP_PORT="${E2E_QDRANT_HTTP_PORT:-16333}"
QDRANT_GRPC_PORT="${E2E_QDRANT_GRPC_PORT:-16334}"
REDIS_PORT="${E2E_REDIS_PORT:-16379}"

ADMIN_EMAIL="${E2E_ADMIN_EMAIL:-e2e-admin@example.test}"
ADMIN_PASSWORD="${E2E_ADMIN_PASSWORD:-E2e-Admin-2026!}"
WAIT_TIMEOUT="${E2E_WAIT_TIMEOUT:-180}"

# The gate never exercises the GPU, and GitHub runners have no
# nvidia-container-toolkit, so request zero GPUs (see x-api-resources in
# docker-compose.prod.yml). Set E2E_GPU_COUNT=all to use the host's GPUs.
GPU_COUNT="${E2E_GPU_COUNT:-0}"

# Secrets. Throwaway values by default so the gate runs on a clean checkout;
# a caller (CI) that exports its own wins, because a real environment variable
# takes precedence over both these defaults and --env-file.
export POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-e2e-postgres-password}"
export JWT_SECRET_KEY="${JWT_SECRET_KEY:-e2e-jwt-secret-that-is-longer-than-32-bytes}"
export REDIS_PASSWORD="${REDIS_PASSWORD:-e2e-redis-password}"

BASE_URL="${E2E_BASE_URL:-http://127.0.0.1:${CLIENT_PORT}}"

usage() { sed -n '3,39p' "$0"; }

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "ERROR: '$1' is required but not installed." >&2; exit 1; }
}

# Every docker compose invocation below goes through this, so the file, the
# env-file and above all the isolated project name can never drift apart.
compose() {
  docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" -p "$PROJECT" "$@"
}

e2e_up() {
  need docker
  if ! docker info >/dev/null 2>&1; then
    echo "ERROR: Docker daemon is not running." >&2
    exit 1
  fi
  echo "==> Starting e2e stack '$PROJECT' from $COMPOSE_FILE (client :$CLIENT_PORT, api :$API_PORT)"
  BOOTSTRAP_ADMIN_ENABLED=true \
  BOOTSTRAP_ADMIN_EMAIL="$ADMIN_EMAIL" \
  BOOTSTRAP_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  API_HOST_PORT="$API_PORT" \
  CLIENT_HOST_PORT="$CLIENT_PORT" \
  POSTGRES_HOST_PORT="$POSTGRES_PORT" \
  QDRANT_HTTP_HOST_PORT="$QDRANT_HTTP_PORT" \
  QDRANT_GRPC_HOST_PORT="$QDRANT_GRPC_PORT" \
  REDIS_HOST_PORT="$REDIS_PORT" \
  API_GPU_COUNT="$GPU_COUNT" \
    compose up -d --wait --wait-timeout "$WAIT_TIMEOUT"
  echo "==> Up. Client: $BASE_URL   API: http://127.0.0.1:${API_PORT}/health/live"
}

e2e_test() {
  need npm
  echo "==> Running the fullstack Playwright suite against $BASE_URL"
  (
    cd LmKitOmniClient
    E2E_BASE_URL="$BASE_URL" \
    E2E_ADMIN_EMAIL="$ADMIN_EMAIL" \
    E2E_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
      npm run test:e2e:fullstack
  )
}

# Safe by construction: -p pins the throwaway project, so `-v` can only ever
# delete volumes this script created. It cannot touch a developer's default
# project or their real data.
e2e_down() {
  echo "==> Tearing down e2e stack '$PROJECT' (its volumes only)"
  compose down -v --remove-orphans
}

case "${1:-}" in
  up)   e2e_up ;;
  test) e2e_test ;;
  down) e2e_down ;;
  ci)
    e2e_up
    status=0
    e2e_test || status=$?
    e2e_down || true
    exit "$status"
    ;;
  -h|--help|"") usage; [ -z "${1:-}" ] && exit 2 || exit 0 ;;
  *) echo "Unknown command: $1 (use up, test, down, ci; see --help)" >&2; exit 2 ;;
esac
