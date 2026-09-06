#!/usr/bin/env bash
# =============================================================================
# Build & push LM-Kit Omni Agent images to Docker Hub (Linux / macOS / WSL).
# Windows equivalent: scripts/build-push.bat
#
# Images produced (override repos via env: API_REPO / CLIENT_REPO):
#   $DOCKER_USER/lmkit-api     — API image, slim CUDA-runtime variant (~3.8GB):
#     ubuntu:22.04 + ASP.NET 10 runtime + CUDA 12+13 runtime libs (cudart+cublas)
#     for the LM-Kit CUDA backends. libcuda.so.1 (driver) comes from the host
#     via nvidia-container-toolkit; no GPU -> CPU fallback. Full nvidia/cuda
#     base (~4.4GB) still available with API_TARGET=final.
#   $DOCKER_USER/lmkit-client  — Vue + Nginx frontend
#
# Usage:
#   ./scripts/build-push.sh                      # build + push both images, tag "latest"
#   ./scripts/build-push.sh --tag v1.2.0         # custom tag (also pushed: git sha)
#   ./scripts/build-push.sh api client           # only build+push the listed targets
#   ./scripts/build-push.sh --no-push            # build only (CI / local e2e)
#   ./scripts/build-push.sh --help
#
# Environment:
#   DOCKER_USER      Docker Hub namespace   (default: duydndev)
#   API_TARGET       API dockerfile target  (default: final-slim; use final
#                    for the full nvidia/cuda base image)
#
# After pushing, deployments update without touching docker-compose.yml:
#   docker compose pull && docker compose up -d
# =============================================================================
set -euo pipefail

cd "$(dirname "$0")/.."

DOCKER_USER="${DOCKER_USER:-duydndev}"
API_REPO="${API_REPO:-${DOCKER_USER}/lmkit-api}"
CLIENT_REPO="${CLIENT_REPO:-${DOCKER_USER}/lmkit-client}"

# Slim CUDA-runtime target by default (see header). Override with API_TARGET=final
# for the full nvidia/cuda base image.
API_TARGET="${API_TARGET:-final-slim}"

TAG="latest"
PUSH=1
TARGETS=(api client)

usage() {
  sed -n '3,24p' "$0"
}
while [ $# -gt 0 ]; do
  case "$1" in
    --tag) TAG="$2"; shift 2 ;;
    --no-push) PUSH=0; shift ;;
    api|client) TARGETS=("$@"); shift $# ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
  esac
done

COMMIT="$(git rev-parse --short=7 HEAD 2>/dev/null || echo dev)"

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "ERROR: '$1' is required but not installed." >&2; exit 1; }
}
need docker
need git

if ! docker info >/dev/null 2>&1; then
  echo "ERROR: Docker daemon is not running." >&2
  exit 1
fi

BUILT_REPOS=()
for target in "${TARGETS[@]}"; do
  case "$target" in
    api)
      echo "==> Building $API_REPO:$TAG (target=$API_TARGET)"
      docker build -f LmKitOmniApi/Dockerfile \
        --target "$API_TARGET" \
        -t "$API_REPO:$TAG" -t "$API_REPO:$COMMIT" \
        .
      BUILT_REPOS+=("$API_REPO") ;;
    client)
      echo "==> Building $CLIENT_REPO:$TAG"
      docker build -t "$CLIENT_REPO:$TAG" -t "$CLIENT_REPO:$COMMIT" LmKitOmniClient
      BUILT_REPOS+=("$CLIENT_REPO") ;;
    *) echo "Unknown target: $target (use api, client)" >&2; exit 2 ;;
  esac
done

if [ "$PUSH" -eq 1 ]; then
  for repo in "${BUILT_REPOS[@]}"; do
    echo "==> Pushing $repo:$TAG and $repo:$COMMIT"
    docker push "$repo:$TAG" || { echo "Push failed — run 'docker login' first." >&2; exit 1; }
    docker push "$repo:$COMMIT"
  done
  echo "==> Done. Deploy updates with: docker compose pull && docker compose up -d"
else
  echo "==> Built (not pushed):"
  for repo in "${BUILT_REPOS[@]}"; do
    echo "    $repo:$TAG (and :$COMMIT)"
  done
fi
