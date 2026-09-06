# syntax=docker/dockerfile:1
# =============================================================================
# LM-Kit Omni API — Linux container (Ubuntu Server deployments)
#
# DEFAULT target: `final-slim` (~3.8GB) — ubuntu:22.04 + ASP.NET 10 runtime +
#   the CUDA runtime libs (cudart + cublas, generations 12 AND 13) that the
#   LM-Kit CUDA backends dlopen. cublas alone is ~450MB per generation, which
#   is why this is not smaller; it still beats the full nvidia/cuda base (~4.4GB)
#   AND ships the CUDA .so files the fat base was missing. The GPU driver
#   (libcuda.so.1) is injected at runtime by nvidia-container-toolkit; without
#   a GPU the SDK falls back to CPU (Vulkan needs libvulkan which is absent in
#   containerized runs). Build scripts use this target:
#   docker build --target final-slim -f LmKitOmniApi/Dockerfile -t lmkit-api .
#   (Trim ~1.4GB by deleting the two cuda-*-12-9 packages if all your hosts
#   run driver >= 580, i.e. CUDA 13-capable.)
#
# FULL CUDA base target: `final` (~4.4GB, nvidia/cuda:13.1-runtime base):
#   docker build --target final -f LmKitOmniApi/Dockerfile -t lmkit-api .
#
# The LM-Kit.NET Linux backend NuGet packages (LM-Kit.NET.Backend.Cuda13.Linux /
# Cuda12.Linux) ship the backend .so files; those dlopen libcudart.so.13 /
# libcublas.so.13 (and the driver lib at runtime), which is why CUDA runtime
# packages are present in both runtime stages.
#
# NOTE: ARG BASE_IMAGE must stay BEFORE the first FROM (global scope) — an ARG
# placed after a FROM belongs to that stage and FROM ${BASE_IMAGE} would see
# an empty value.
# =============================================================================

ARG BASE_IMAGE=nvidia/cuda:13.1.0-runtime-ubuntu22.04

# ── Build stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# NOTE: use -p:RuntimeIdentifier (NOT -r) on restore. With the .NET 10 SDK,
# `restore -r linux-x64` skips RID-conditional PackageReferences (the LM-Kit
# CUDA backend .so packages never enter project.assets.json), while
# `-p:RuntimeIdentifier=linux-x64` includes them. The publish step then just
# copies the already-restored native assets.
COPY ["LmKitOmniApi/LmKitOmniApi.csproj", "LmKitOmniApi/"]
RUN dotnet restore "LmKitOmniApi/LmKitOmniApi.csproj" -p:RuntimeIdentifier=linux-x64

COPY . .
WORKDIR "/src/LmKitOmniApi"
RUN dotnet publish "LmKitOmniApi.csproj" -c Release -p:RuntimeIdentifier=linux-x64 --no-restore \
    --self-contained false -o /app/publish

# ── Runtime stage ───────────────────────────────────────────────────────────
# BASE_IMAGE: nvidia/cuda:13.1.0-runtime-ubuntu22.04 by default (provides the
# CUDA runtime the Linux backend .so files link against). CPU/Vulkan builds
# pass mcr.microsoft.com/dotnet/aspnet:10.0 instead. The nvidia/cuda 22.04
# image already contains the required runtime; we add the minimal .NET runtime
# bits via the aspnet runtime install script so the image stays single-stage.
FROM ${BASE_IMAGE} AS final

# .NET runtime for nvidia/cuda bases (no-op on the plain aspnet base because the
# dotnet runtime already exists at /usr/share/dotnet — the script detects it).
# The nvidia/cuda ubuntu22.04 base does NOT ship libicu, which .NET requires for
# globalization — install libicu70 alongside. Install script pinned to .NET 10
# channel; curl is needed by the script itself.
RUN if ! command -v dotnet >/dev/null 2>&1; then \
        apt-get update && apt-get install -y --no-install-recommends curl ca-certificates libicu70 \
        && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
        && chmod +x /tmp/dotnet-install.sh \
        && /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet \
        && rm /tmp/dotnet-install.sh \
        && rm -rf /var/lib/apt/lists/* \
        && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet; \
    fi

WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
RUN mkdir -p /app/Models /app/Uploads /app/App_Data /var/lib/lmkit/keys \
    && chown -R 1654:1654 /app/Models /app/Uploads /app/App_Data /var/lib/lmkit/keys \
    && chmod 700 /var/lib/lmkit/keys

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_TieredPGO=1

EXPOSE 5000
USER 1654
ENTRYPOINT ["dotnet", "LmKitOmniApi.dll"]

# ── Runtime stage, slim variant (DEFAULT for build-push scripts) ────────────
# Reuses the SAME build stage above; ~550MB total instead of ~4.4GB.
# The full nvidia/cuda base pulls the entire CUDA toolkit while the LM-Kit
# CUDA13 backend only dlopens libcudart.so.13 and libcublas.so.13 — so this
# stage installs exactly those two packages from the official NVIDIA repo.
# libcuda.so.1 (the user-space driver) is injected at runtime by
# nvidia-container-toolkit from the host driver, as with any CUDA container.
# Build explicitly:  docker build --target final-slim -f LmKitOmniApi/Dockerfile .
FROM ubuntu:22.04 AS final-slim

ENV NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    ASPNETCORE_URLS=http://+:5000 \
    DOTNET_TieredPGO=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    LD_LIBRARY_PATH=/usr/local/cuda-13.1/lib64

# CUDA runtime bits for BOTH CUDA generations the LM-Kit backends dlopen:
#   cuda13 .so -> libcudart.so.13 / libcublas.so.13  (driver >= 580)
#   cuda12 .so -> libcudart.so.12 / libcublas.so.12  (older drivers, >= 525)
# Packages come from the official NVIDIA apt repo (added via cuda-keyring),
# the same source the nvidia/cuda images install from.
# + libicu70 (required by .NET globalization, absent on ubuntu:22.04)
# + curl (healthcheck + dotnet-install script)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.1-1_all.deb -o /tmp/cuda-keyring.deb \
    && dpkg -i /tmp/cuda-keyring.deb && rm -f /tmp/cuda-keyring.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        libicu70 \
        cuda-cudart-13-1 libcublas-13-1 \
        cuda-cudart-12-9 libcublas-12-9 \
    && rm -rf /var/lib/apt/lists/*

# ASP.NET Core 10.0 runtime at /usr/share/dotnet
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/Models /app/Uploads /app/App_Data /var/lib/lmkit/keys \
    && chown -R 1654:1654 /app/Models /app/Uploads /app/App_Data /var/lib/lmkit/keys \
    && chmod 700 /var/lib/lmkit/keys

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

EXPOSE 5000
USER 1654
ENTRYPOINT ["dotnet", "LmKitOmniApi.dll"]
