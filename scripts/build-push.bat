@echo off
rem =============================================================================
rem Build ^& push LM-Kit Omni Agent images to Docker Hub (Windows).
rem Linux / macOS equivalent: scripts/build-push.sh
rem
rem Images produced (override repos via env: API_REPO / CLIENT_REPO):
rem   %DOCKER_USER%/lmkit-api     - API image, slim CUDA-runtime variant (~3.8GB):
rem     ubuntu:22.04 + ASP.NET 10 runtime + CUDA 12+13 runtime libs (cudart+cublas)
rem     for the LM-Kit CUDA backends. libcuda.so.1 (driver) comes from the host
rem     via nvidia-container-toolkit; no GPU -> CPU fallback. Full nvidia/cuda
rem     base (~4.4GB) still available with API_TARGET=final.
rem
rem Usage:
rem   scripts\build-push.bat                        build + push both, tag "latest"
rem   scripts\build-push.bat --tag v1.2.0           custom tag (git sha also pushed)
rem   scripts\build-push.bat api client             only the listed targets
rem   scripts\build-push.bat --no-push              build only (CI / local e2e)
rem
rem Environment:
rem   DOCKER_USER      Docker Hub namespace   (default: duydndev)
rem   API_TARGET       API dockerfile target  (default: final-slim; use final
rem                    for the full nvidia/cuda base image)
rem
rem After pushing, deployments update without touching docker-compose.yml:
rem   docker compose pull ^&^& docker compose up -d
rem =============================================================================
setlocal enabledelayedexpansion

rem Run everything from the repo root so relative paths (LmKitOmniApi/Dockerfile,
rem LmKitOmniClient) resolve no matter which directory the script is invoked from.
rem The batch file runs in its own cmd process, so this does not move the caller's shell.
set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%.."
pushd "%ROOT%" >nul

rem Docker Hub namespace - override by pre-setting DOCKER_USER in the environment.
if not defined DOCKER_USER set "DOCKER_USER=duydndev"
set "API_REPO=%DOCKER_USER%/lmkit-api"
set "CLIENT_REPO=%DOCKER_USER%/lmkit-client"
rem Slim CUDA runtime target by default (see header). Override with API_TARGET=final
rem for the full nvidia/cuda base image.
if not defined API_TARGET set "API_TARGET=final-slim"

set "TAG=latest"
set "PUSH=1"
set "TARGETS=api client"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--tag" (
  set "TAG=%~2"
  shift
  shift
  goto parse
)
if /i "%~1"=="--no-push" (
  set "PUSH=0"
  shift
  goto parse
)
if /i "%~1"=="-h" goto :help
if /i "%~1"=="--help" goto :help
if /i "%~1"=="api" (
  if not defined TARGETS_SET ( set "TARGETS=" & set "TARGETS_SET=1" )
  set "TARGETS=!TARGETS! api"
  shift
  goto parse
)
if /i "%~1"=="client" (
  if not defined TARGETS_SET ( set "TARGETS=" & set "TARGETS_SET=1" )
  set "TARGETS=!TARGETS! client"
  shift
  goto parse
)
echo Unknown option: %~1  (see --help^)
exit /b 2
:parsed

rem git commit tag (best effort)
set "COMMIT=dev"
for /f %%i in ('git rev-parse --short^=7 HEAD 2^>nul') do set "COMMIT=%%i"

where docker >nul 2>&1
if errorlevel 1 (
  echo ERROR: 'docker' is required but not installed or not on PATH.
  exit /b 1
)
docker info >nul 2>&1
if errorlevel 1 (
  echo ERROR: Docker daemon is not running. Start Docker Desktop first.
  exit /b 1
)

set "BUILT="
for %%T in (%TARGETS%) do (
  if /i "%%T"=="api" (
    call :build "%API_REPO%"
    if errorlevel 1 exit /b 1
    set "BUILT=!BUILT! %API_REPO%"
  ) else if /i "%%T"=="client" (
    call :buildclient
    if errorlevel 1 exit /b 1
    set "BUILT=!BUILT! %CLIENT_REPO%"
  ) else (
    echo Unknown target: %%T ^(use api, client^)
    exit /b 2
  )
)

if "%PUSH%"=="1" (
  for %%R in (%BUILT%) do (
    echo ==^> Pushing %%R:%TAG% and %%R:%COMMIT%
    docker push "%%R:%TAG%"
    if errorlevel 1 (
      echo Push failed - run 'docker login' first.
      exit /b 1
    )
    docker push "%%R:%COMMIT%"
  )
  echo ==^> Done. Deploy updates with: docker compose pull ^&^& docker compose up -d
) else (
  echo ==^> Built ^(not pushed^):
  for %%R in (%BUILT%) do echo     %%R:%TAG% ^(and :%COMMIT%^)
)
exit /b 0

:build
echo ==^> Building %~1:%TAG% ^(target=%API_TARGET%^)
docker build -f LmKitOmniApi/Dockerfile --target %API_TARGET% -t "%~1:%TAG%" -t "%~1:%COMMIT%" .
if errorlevel 1 exit /b 1
goto :eof

:buildclient
echo ==^> Building %CLIENT_REPO%:%TAG%
docker build -t "%CLIENT_REPO%:%TAG%" -t "%CLIENT_REPO%:%COMMIT%" LmKitOmniClient
if errorlevel 1 exit /b 1
goto :eof

:help
echo Usage: scripts\build-push.bat [--tag TAG] [--no-push] [api] [client]
echo   Default: build + push both images with tag "latest".
echo   The API image is the slim CUDA-runtime variant (~3.8GB, target final-slim);
echo   set API_TARGET=final for the full nvidia/cuda base (~4.4GB).
echo   Env overrides: DOCKER_USER, API_REPO, CLIENT_REPO, API_BASE_IMAGE
exit /b 0
