@echo off
rem =============================================================================
rem Isolated full-stack browser gate for LM-Kit Omni Agent (Windows).
rem Linux / macOS equivalent: scripts/e2e-fullstack.sh
rem
rem ONE compose file. There is no e2e compose file, no override file and no
rem second stack behind a profile: this runs docker-compose.prod.yml - the same
rem file that ships production - and makes it side-by-side safe purely with
rem environment variables plus a separate Compose project name. See the mode-3
rem header comment in docker-compose.yml.
rem
rem The .sh twin is what CI runs; keep the defaults below identical to it.
rem
rem Usage:
rem   scripts\e2e-fullstack.bat up            start the stack, wait for healthy
rem   scripts\e2e-fullstack.bat test          run the Playwright fullstack suite
rem   scripts\e2e-fullstack.bat down          stop it, delete ITS volumes only
rem   scripts\e2e-fullstack.bat ci            up + test + down (down always runs)
rem
rem Typical local run (images must exist first):
rem   scripts\build-push.bat --no-push api client
rem   scripts\e2e-fullstack.bat up ^&^& scripts\e2e-fullstack.bat test
rem   scripts\e2e-fullstack.bat down
rem
rem Environment overrides (all optional - defaults below are the CI values):
rem   E2E_PROJECT, E2E_CLIENT_PORT, E2E_API_PORT, E2E_POSTGRES_PORT,
rem   E2E_QDRANT_HTTP_PORT, E2E_QDRANT_GRPC_PORT, E2E_REDIS_PORT,
rem   E2E_ADMIN_EMAIL, E2E_ADMIN_PASSWORD, E2E_WAIT_TIMEOUT, E2E_GPU_COUNT
rem =============================================================================
setlocal enabledelayedexpansion

rem Run from the repo root so docker-compose.prod.yml and LmKitOmniClient resolve
rem no matter which directory the script is invoked from. The batch file runs in
rem its own cmd process, so this does not move the caller's shell.
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%.." >nul

rem -- The single source of truth for e2e configuration ------------------------
if not defined E2E_PROJECT set "E2E_PROJECT=lmkit-fullstack-e2e"
set "COMPOSE_FILE=docker-compose.prod.yml"
if not defined E2E_ENV_FILE set "E2E_ENV_FILE=.env.example"

if not defined E2E_CLIENT_PORT      set "E2E_CLIENT_PORT=18080"
if not defined E2E_API_PORT         set "E2E_API_PORT=15032"
if not defined E2E_POSTGRES_PORT    set "E2E_POSTGRES_PORT=15432"
if not defined E2E_QDRANT_HTTP_PORT set "E2E_QDRANT_HTTP_PORT=16333"
if not defined E2E_QDRANT_GRPC_PORT set "E2E_QDRANT_GRPC_PORT=16334"
if not defined E2E_REDIS_PORT       set "E2E_REDIS_PORT=16379"

if not defined E2E_ADMIN_EMAIL    set "E2E_ADMIN_EMAIL=e2e-admin@example.test"
if not defined E2E_ADMIN_PASSWORD set "E2E_ADMIN_PASSWORD=E2e-Admin-2026!"
if not defined E2E_WAIT_TIMEOUT   set "E2E_WAIT_TIMEOUT=180"

rem The gate never exercises the GPU (see x-api-resources in the prod compose
rem file). Set E2E_GPU_COUNT=all to use the host's GPUs.
if not defined E2E_GPU_COUNT set "E2E_GPU_COUNT=0"

rem Throwaway secrets so the gate runs on a clean checkout; a caller that already
rem exported its own wins, because a real environment variable takes precedence
rem over both these defaults and --env-file.
if not defined POSTGRES_PASSWORD set "POSTGRES_PASSWORD=e2e-postgres-password"
if not defined JWT_SECRET_KEY    set "JWT_SECRET_KEY=e2e-jwt-secret-that-is-longer-than-32-bytes"
if not defined REDIS_PASSWORD    set "REDIS_PASSWORD=e2e-redis-password"

if not defined E2E_BASE_URL set "E2E_BASE_URL=http://127.0.0.1:%E2E_CLIENT_PORT%"

if /i "%~1"=="up"   goto :up
if /i "%~1"=="test" goto :test
if /i "%~1"=="down" goto :down
if /i "%~1"=="ci"   goto :ci
if /i "%~1"=="-h"     goto :help
if /i "%~1"=="--help" goto :help
if "%~1"=="" goto :help
echo Unknown command: %~1  ^(use up, test, down, ci^)
popd >nul
exit /b 2

:up
where docker >nul 2>&1
if errorlevel 1 (
  echo ERROR: 'docker' is required but not installed or not on PATH.
  popd >nul
  exit /b 1
)
docker info >nul 2>&1
if errorlevel 1 (
  echo ERROR: Docker daemon is not running. Start Docker Desktop first.
  popd >nul
  exit /b 1
)
echo ==^> Starting e2e stack '%E2E_PROJECT%' from %COMPOSE_FILE% ^(client :%E2E_CLIENT_PORT%, api :%E2E_API_PORT%^)
set "BOOTSTRAP_ADMIN_ENABLED=true"
set "BOOTSTRAP_ADMIN_EMAIL=%E2E_ADMIN_EMAIL%"
set "BOOTSTRAP_ADMIN_PASSWORD=%E2E_ADMIN_PASSWORD%"
set "API_HOST_PORT=%E2E_API_PORT%"
set "CLIENT_HOST_PORT=%E2E_CLIENT_PORT%"
set "POSTGRES_HOST_PORT=%E2E_POSTGRES_PORT%"
set "QDRANT_HTTP_HOST_PORT=%E2E_QDRANT_HTTP_PORT%"
set "QDRANT_GRPC_HOST_PORT=%E2E_QDRANT_GRPC_PORT%"
set "REDIS_HOST_PORT=%E2E_REDIS_PORT%"
set "API_GPU_COUNT=%E2E_GPU_COUNT%"
docker compose -f "%COMPOSE_FILE%" --env-file "%E2E_ENV_FILE%" -p "%E2E_PROJECT%" up -d --wait --wait-timeout %E2E_WAIT_TIMEOUT%
if errorlevel 1 (
  popd >nul
  exit /b 1
)
echo ==^> Up. Client: %E2E_BASE_URL%   API: http://127.0.0.1:%E2E_API_PORT%/health/live
popd >nul
exit /b 0

:test
echo ==^> Running the fullstack Playwright suite against %E2E_BASE_URL%
pushd LmKitOmniClient >nul
call npm run test:e2e:fullstack
set "TEST_RC=%ERRORLEVEL%"
popd >nul
popd >nul
exit /b %TEST_RC%

rem Safe by construction: -p pins the throwaway project, so -v can only ever
rem delete volumes this script created. It cannot touch a developer's default
rem project or their real data.
:down
echo ==^> Tearing down e2e stack '%E2E_PROJECT%' ^(its volumes only^)
docker compose -f "%COMPOSE_FILE%" --env-file "%E2E_ENV_FILE%" -p "%E2E_PROJECT%" down -v --remove-orphans
set "DOWN_RC=%ERRORLEVEL%"
popd >nul
exit /b %DOWN_RC%

:ci
call "%~f0" up
if errorlevel 1 (
  call "%~f0" down
  popd >nul
  exit /b 1
)
call "%~f0" test
set "TEST_RC=%ERRORLEVEL%"
call "%~f0" down
popd >nul
exit /b %TEST_RC%

:help
echo Usage: scripts\e2e-fullstack.bat [up^|test^|down^|ci]
echo   up    - start docker-compose.prod.yml as project '%E2E_PROJECT%' and wait for healthy
echo   test  - run npm run test:e2e:fullstack against %E2E_BASE_URL%
echo   down  - stop that project and delete only its volumes
echo   ci    - up + test + down
popd >nul
exit /b 0
