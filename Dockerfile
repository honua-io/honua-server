# syntax=docker/dockerfile:1.7
# Auxiliary JIT Dockerfile for development, conformance, and compatibility debugging.
# Production serving images use docker/Dockerfile.aot; canonical release tags never use this file.
# Enhanced security: minimal attack surface, non-root user, read-only filesystem

# Base images are digest-pinned for reproducible builds and supply-chain integrity.
# Refresh both digests together by resolving the current manifests behind
# `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0`.
# Overrideable via build args so CI can swap in pre-warmed mirrors if MCR throttles.
# These ARG defaults are the single source of truth for the mirrored bases:
# scripts/ci/base-image-mirrors.sh reads them and the nightly `mirror-base-images`
# job mirrors exactly what it prints, so no second digest list needs updating.
# digest pinned 2026-08-12
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
# digest pinned 2026-08-20
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:a9298ccc39c6258ae71228479e2f3f8d2ceb6639a9927262a4b0ff7d319a0e7e

# Build stage
# Extract restore inputs with their directory structure. Ordinary source edits,
# including JSON content, rerun this inexpensive extraction but leave the
# downstream restore input tree unchanged.
FROM ${DOTNET_ASPNET_IMAGE} AS chiseled-base

FROM ${DOTNET_SDK_IMAGE} AS restore-inputs
# Bind-mounted context. WORKDIR avoids `cd` (hadolint DL3003) without changing
# the relative paths `find` and `cp --parents` write under /restore-inputs.
WORKDIR /context
RUN --mount=type=bind,target=/context \
    mkdir -p /restore-inputs && \
    find . -type d \( -name bin -o -name obj -o -name .git \) -prune -o \
      -type f \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \
        -o -name '*.props' -o -name '*.targets' \
        -o -name '*.sln' -o -name '*.slnx' -o -name '*.slnf' \
        -o -name '*.config' -o -name '.editorconfig' \
        -o -name 'global.json' -o -name 'packages.lock.json' -o -name 'dotnet-tools.json' \) \
      -exec cp --parents -p '{}' /restore-inputs/ \;

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Security: Create non-root build user
RUN groupadd --gid 1001 --system builduser && \
    useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin builduser

# Restore sees project/configuration inputs only. The extraction stage tracks
# additions automatically without tying NuGet restore to every source edit.
COPY --from=restore-inputs /restore-inputs/ ./
COPY eng/Honua.BuildProfiles.props eng/
COPY eng/Honua.ProtocolProfiles.props eng/
COPY eng/protocol-profiles.v1.json eng/
COPY scripts/docker/restore-dotnet-with-github-packages.sh scripts/docker/

ARG TARGETARCH
ARG HONUA_STORE_PROFILE=postgis
ARG HONUA_BUILD_PROFILE=full
ARG HONUA_PROTOCOL_PROFILE=all
ARG HONUA_INCLUDE_AWS=
ARG HONUA_INCLUDE_AZURE=
ARG HONUA_INCLUDE_ORACLE=
ARG HONUA_INCLUDE_SNOWFLAKE=

# Restore dependencies.
# SC2086 suppression rationale: EXTRA_MSBUILD_ARGS holds multiple MSBuild flags that must
# word-split into separate `-p:` arguments. Quoting collapses them into a single (invalid) argument.
# hadolint ignore=SC2086
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-x64" ;; \
        arm64) RUNTIME_ID="linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    MODULE_MSBUILD_ARGS="-p:HonuaStoreProfile=${HONUA_STORE_PROFILE:-postgis} -p:HonuaUseSkiaNoDependencies=true -p:HonuaBuildProfile=${HONUA_BUILD_PROFILE:-full} -p:HonuaProtocolProfile=${HONUA_PROTOCOL_PROFILE:-all}" && \
    if [ -n "${HONUA_INCLUDE_AWS:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAws=$HONUA_INCLUDE_AWS"; fi && \
    if [ -n "${HONUA_INCLUDE_AZURE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAzure=$HONUA_INCLUDE_AZURE"; fi && \
    if [ -n "${HONUA_INCLUDE_ORACLE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeOracle=$HONUA_INCLUDE_ORACLE"; fi && \
    if [ -n "${HONUA_INCLUDE_SNOWFLAKE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeSnowflake=$HONUA_INCLUDE_SNOWFLAKE"; fi && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaSkipRepoAnalyzers=true -p:RunAnalyzersDuringBuild=false $MODULE_MSBUILD_ARGS" && \
    sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj \
      --runtime "$RUNTIME_ID" \
      $EXTRA_MSBUILD_ARGS

# Copy source code
COPY . .

# Build application (disable AOT for default image).
# SC2086 suppression rationale: EXTRA_MSBUILD_ARGS holds multiple MSBuild flags that must
# word-split into separate `-p:` arguments. Quoting collapses them into a single (invalid) argument.
ARG CONFIGURATION=Release
# Empty VERSION preserves Directory.Build.props for local container builds.
ARG VERSION=
ARG HONUA_GIT_SHA=
# hadolint ignore=SC2086
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-x64" ;; \
        arm64) RUNTIME_ID="linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    MODULE_MSBUILD_ARGS="-p:HonuaStoreProfile=${HONUA_STORE_PROFILE:-postgis} -p:HonuaUseSkiaNoDependencies=true -p:HonuaBuildProfile=${HONUA_BUILD_PROFILE:-full} -p:HonuaProtocolProfile=${HONUA_PROTOCOL_PROFILE:-all}" && \
    if [ -n "${HONUA_INCLUDE_AWS:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAws=$HONUA_INCLUDE_AWS"; fi && \
    if [ -n "${HONUA_INCLUDE_AZURE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeAzure=$HONUA_INCLUDE_AZURE"; fi && \
    if [ -n "${HONUA_INCLUDE_ORACLE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeOracle=$HONUA_INCLUDE_ORACLE"; fi && \
    if [ -n "${HONUA_INCLUDE_SNOWFLAKE:-}" ]; then MODULE_MSBUILD_ARGS="$MODULE_MSBUILD_ARGS -p:HonuaIncludeSnowflake=$HONUA_INCLUDE_SNOWFLAKE"; fi && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaSkipRepoAnalyzers=true -p:RunAnalyzersDuringBuild=false $MODULE_MSBUILD_ARGS" && \
    sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj \
      --runtime "$RUNTIME_ID" \
      $EXTRA_MSBUILD_ARGS && \
    dotnet publish src/Honua.Server/Honua.Server.csproj \
      ${VERSION:+-p:Version="$VERSION"} \
      ${VERSION:+-p:InformationalVersion="$VERSION$(if [ -n "$HONUA_GIT_SHA" ]; then printf '+%.7s' "$HONUA_GIT_SHA"; fi)"} \
      ${VERSION:+-p:IncludeSourceRevisionInInformationalVersion=false} \
      --configuration "$CONFIGURATION" \
      --runtime "$RUNTIME_ID" \
      --no-restore \
      --self-contained false \
      --output /app \
      -p:PublishAot=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -p:HonuaNobleRuntimePublish=true \
      $EXTRA_MSBUILD_ARGS && \
    rm -rf /app/wwwroot/admin && \
    find /app -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

# Runtime stage
FROM ${DOTNET_SDK_IMAGE} AS runtime-assets
WORKDIR /src
COPY docker/runtime/ docker/runtime/
COPY --from=chiseled-base / /runtime-root/
# hadolint ignore=DL3008
RUN apt-get update && \
    apt-get install -y --no-install-recommends gcc libatomic1 libgssapi-krb5-2 fonts-dejavu-core && \
    sh docker/runtime/stage-runtime.sh /runtime-root /runtime-additions && \
    rm -rf /var/lib/apt/lists/*
COPY --from=build /app /runtime-root/app
RUN sh docker/runtime/verify-native.sh /runtime-root /app

FROM chiseled-base AS runtime
COPY --from=runtime-assets /runtime-additions/ /


WORKDIR /app
COPY --from=build --chown=1001:1001 /app .
COPY --chown=1001:1001 LICENSE THIRD-PARTY-NOTICES.md /app/

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
      org.opencontainers.image.licenses="LicenseRef-FCL-1.0-ALv2" \
      honua.runtime.profile="web-debug" \
      honua.runtime.compilation="jit" \
      honua.runtime.distribution="non-production"

# Immutable deployment identity (#3038). `.dockerignore` excludes `.git/`, so SourceLink
# cannot stamp a commit into the published assemblies from inside the build context; the
# commit SHA is therefore injected here as a runtime environment variable. Placing the ARG
# in the runtime stage keeps a SHA change from invalidating the expensive build layers.
# HONUA_IMAGE_DIGEST is intentionally not set here: an image cannot contain its own digest,
# so deployments inject it (Helm/Terraform/task definition) once the image has been pushed.
ARG HONUA_GIT_SHA=
ARG VERSION=
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.revision="${HONUA_GIT_SHA}"
ENV HONUA_GIT_SHA=${HONUA_GIT_SHA}

ENV ASPNETCORE_ENVIRONMENT=Production \
    HONUA_DEFAULT_FONT_PATH=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf \
    ASPNETCORE_HTTP_PORTS= \
    Kestrel__Endpoints__Http__Url=http://+:8080 \
    Kestrel__Endpoints__Http__Protocols=Http1 \
    Kestrel__Endpoints__Grpc__Url=http://+:8081 \
    Kestrel__Endpoints__Grpc__Protocols=Http2 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0

# The compiled probe runs without a shell.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["/app/honua-healthcheck"]

EXPOSE 8080/tcp 8081/tcp

ENTRYPOINT ["dotnet", "Honua.Server.dll"]
