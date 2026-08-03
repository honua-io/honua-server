# syntax=docker/dockerfile:1.7
# Auxiliary JIT Dockerfile for development, conformance, and compatibility debugging.
# Production serving images use docker/Dockerfile.aot; canonical release tags never use this file.
# Enhanced security: minimal attack surface, non-root user, read-only filesystem

# Base images are digest-pinned for reproducible builds and supply-chain integrity.
# Refresh both digests together by resolving the current manifests behind
# `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`.
# Overrideable via build args so CI can swap in pre-warmed mirrors if MCR throttles.
# These ARG defaults are the single source of truth for the mirrored bases:
# scripts/ci/base-image-mirrors.sh reads them and the nightly `mirror-base-images`
# job mirrors exactly what it prints, so no second digest list needs updating.
# digest pinned 2026-07-24
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
# digest pinned 2026-07-24
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a

# Build stage
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Security: Create non-root build user
RUN groupadd --gid 1001 --system builduser && \
    useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin builduser

# Copy solution and source project graph first for restore. The modularized
# assembly graph changes often enough that a hand-maintained csproj list is
# more brittle than copying src/ into the restore layer.
COPY Honua.sln Directory.Build.props Directory.Packages.props NuGet.config .editorconfig ./
COPY eng/Honua.BuildProfiles.props eng/
COPY scripts/docker/restore-dotnet-with-github-packages.sh scripts/docker/
COPY src/ src/
COPY docs/developer/api-specs/admin-api.json docs/developer/api-specs/
COPY docs/gis/data/feature-catalog.json docs/gis/data/
COPY samples/Honua.StacOpsDemo/*.csproj samples/Honua.StacOpsDemo/

ARG TARGETARCH
# Slim by default: set HONUA_INCLUDE_STAC_OPS_DEMO=true to keep the hosted STAC ops demo assets.
ARG HONUA_INCLUDE_STAC_OPS_DEMO=false
ARG HONUA_BUILD_PROFILE=full
ARG HONUA_INCLUDE_AWS=
ARG HONUA_INCLUDE_AZURE=
ARG HONUA_INCLUDE_ORACLE=
ARG HONUA_INCLUDE_SNOWFLAKE=

# Restore dependencies.
# SC2086 suppression rationale: EXTRA_MSBUILD_ARGS holds multiple MSBuild flags that must
# word-split into separate `-p:` arguments. Quoting collapses them into a single (invalid) argument.
# hadolint ignore=SC2086
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-musl-x64" ;; \
        arm64) RUNTIME_ID="linux-musl-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    MODULE_MSBUILD_ARGS="-p:HonuaBuildProfile=${HONUA_BUILD_PROFILE:-full}" && \
    if [ -n "${HONUA_INCLUDE_AWS:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAws=$HONUA_INCLUDE_AWS"; fi && \
    if [ -n "${HONUA_INCLUDE_AZURE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAzure=$HONUA_INCLUDE_AZURE"; fi && \
    if [ -n "${HONUA_INCLUDE_ORACLE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeOracle=$HONUA_INCLUDE_ORACLE"; fi && \
    if [ -n "${HONUA_INCLUDE_SNOWFLAKE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeSnowflake=$HONUA_INCLUDE_SNOWFLAKE"; fi && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaIncludeStacOpsDemo=false $MODULE_MSBUILD_ARGS" && \
    sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj \
      --runtime "$RUNTIME_ID" \
      $EXTRA_MSBUILD_ARGS && \
    if [ "$HONUA_INCLUDE_STAC_OPS_DEMO" = "true" ]; then \
      sh scripts/docker/restore-dotnet-with-github-packages.sh samples/Honua.StacOpsDemo/Honua.StacOpsDemo.csproj; \
    fi

# Copy source code
COPY . .

# Build application (disable AOT for default image).
# SC2086 suppression rationale: EXTRA_MSBUILD_ARGS holds multiple MSBuild flags that must
# word-split into separate `-p:` arguments. Quoting collapses them into a single (invalid) argument.
ARG CONFIGURATION=Release
# hadolint ignore=SC2086
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-musl-x64" ;; \
        arm64) RUNTIME_ID="linux-musl-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    MODULE_MSBUILD_ARGS="-p:HonuaBuildProfile=${HONUA_BUILD_PROFILE:-full}" && \
    if [ -n "${HONUA_INCLUDE_AWS:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAws=$HONUA_INCLUDE_AWS"; fi && \
    if [ -n "${HONUA_INCLUDE_AZURE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAzure=$HONUA_INCLUDE_AZURE"; fi && \
    if [ -n "${HONUA_INCLUDE_ORACLE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeOracle=$HONUA_INCLUDE_ORACLE"; fi && \
    if [ -n "${HONUA_INCLUDE_SNOWFLAKE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeSnowflake=$HONUA_INCLUDE_SNOWFLAKE"; fi && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaIncludeStacOpsDemo=false $MODULE_MSBUILD_ARGS" && \
    sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj \
      --runtime "$RUNTIME_ID" \
      $EXTRA_MSBUILD_ARGS && \
    dotnet publish src/Honua.Server/Honua.Server.csproj \
      --configuration "$CONFIGURATION" \
      --runtime "$RUNTIME_ID" \
      --no-restore \
      --self-contained false \
      --output /app \
      -p:PublishAot=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      $EXTRA_MSBUILD_ARGS && \
    if [ "$HONUA_INCLUDE_STAC_OPS_DEMO" = "true" ]; then \
      dotnet publish samples/Honua.StacOpsDemo/Honua.StacOpsDemo.csproj \
        --configuration "$CONFIGURATION" \
        --no-restore \
        --output /tmp/stac-ops-demo && \
      mkdir -p /app/wwwroot/samples && \
      cp -a /tmp/stac-ops-demo/wwwroot/samples/stac-ops /app/wwwroot/samples/; \
    fi && \
    rm -rf /tmp/stac-ops-demo && \
    rm -rf /app/BlazorDebugProxy /app/wwwroot/admin && \
    if [ "$HONUA_INCLUDE_STAC_OPS_DEMO" != "true" ]; then rm -rf /app/wwwroot/samples/stac-ops; fi && \
    find /app -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

# Runtime stage
FROM ${DOTNET_ASPNET_IMAGE} AS runtime

# Security: Install runtime dependencies.
# DL3018 suppression rationale: the runtime base image is digest-pinned to a specific Alpine
# snapshot, so apk package versions are deterministic for that snapshot. Pinning here would
# force a parallel apk-version update on every digest bump.
# hadolint ignore=DL3018
RUN apk upgrade --no-cache && \
    apk add --no-cache \
    icu-libs \
    krb5-libs \
    tzdata \
    fontconfig \
    ca-certificates && \
    rm -rf /var/cache/apk/* && \
    rm -rf /tmp/*

# Security: Create non-root user with minimal privileges
RUN addgroup -g 1001 -S honua && \
    adduser -S honua -G honua -u 1001 -s /sbin/nologin -h /app

WORKDIR /app
COPY --from=build --chown=1001:1001 /app .

# Security: Create runtime directories with proper permissions.
# This set MUST include every writable directory the default configuration writes
# to at runtime. Under the read-only root filesystem this image is built for
# (security.read-only-root="true"), any path not provisioned here — and not backed
# by a writable volume mount by the deployment — is read-only, so a write attempt
# throws. /tmp/honua-temp and /tmp/honua-storage back the default temporary and
# local-storage paths; /var/lib/honua/storage is the persistent Compose override.
# These paths back the map-image export href/f=json responses
# (MapServer export, ImageServer exportImage, OGC API Maps); omitting them here made
# every rendered-image export fail with a 500 while inline f=image responses (which
# never touch temp storage) still succeeded. Keep this list in sync with the default
# storage directories in appsettings.json — DockerfileWritableStorageDirectoryTests
# enforces it.
RUN mkdir -p /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics /tmp/honua-temp /tmp/honua-storage /var/lib/honua/storage && \
    chown -R 1001:1001 /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics /tmp/honua-temp /tmp/honua-storage /var/lib/honua/storage && \
    chmod 750 /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics /tmp/honua-temp /tmp/honua-storage /var/lib/honua/storage

# Security: Remove unnecessary setuid/setgid binaries
RUN find / -xdev -perm /6000 -type f -exec chmod a-s {} \; 2>/dev/null || true

USER 1001:1001

# Security and compliance labels
LABEL security.non-root="true" \
      security.capabilities.drop="ALL" \
      security.read-only-root="true" \
      security.user="1001" \
      security.group="1001" \
      maintainer="Honua Development Team" \
      version="1.0" \
      description="Honua Geospatial Feature Server" \
      org.opencontainers.image.source="https://github.com/honua/honua-server" \
      org.opencontainers.image.description="Development and compatibility JIT image; use the native-AOT image for production" \
      org.opencontainers.image.licenses="Elastic-2.0" \
      honua.runtime.profile="web-debug" \
      honua.runtime.compilation="jit" \
      honua.runtime.distribution="non-production"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    Kestrel__Endpoints__Http__Url=http://+:8080 \
    Kestrel__Endpoints__Http__Protocols=Http1 \
    Kestrel__Endpoints__Grpc__Url=http://+:8081 \
    Kestrel__Endpoints__Grpc__Protocols=Http2 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget -q -T 5 -O /dev/null http://localhost:8080/healthz/live || exit 1

EXPOSE 8080/tcp 8081/tcp

ENTRYPOINT ["dotnet", "Honua.Server.dll"]
