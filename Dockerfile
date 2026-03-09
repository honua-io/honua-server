# syntax=docker/dockerfile:1.7
# Multi-stage Dockerfile for Honua Server
# JIT build for maximum compatibility (AOT via docker/Dockerfile.aot)
# Enhanced security: minimal attack surface, non-root user, read-only filesystem

# Build stage
# Use the Debian SDK image so Grpc.Tools/protoc can run during container builds.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# grpc.tools' bundled linux_arm64 protoc segfaults on native ARM runners.
# Use the distro compiler through the supported PROTOBUF_PROTOC override instead.
RUN apt-get update && \
    apt-get install -y --no-install-recommends protobuf-compiler && \
    rm -rf /var/lib/apt/lists/*

# Security: Create non-root build user
RUN groupadd --gid 1001 --system builduser && \
    useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin builduser

# Copy solution and project files first for better layer caching
COPY Honua.sln Directory.Build.props .editorconfig ./
COPY src/Honua.Core/*.csproj src/Honua.Core/
COPY src/Honua.Postgres/*.csproj src/Honua.Postgres/
COPY src/Honua.ServiceDefaults/*.csproj src/Honua.ServiceDefaults/
COPY src/Honua.Server/*.csproj src/Honua.Server/

# Build arguments consumed in restore/publish
ARG TARGETARCH
# Slim by default: set HONUA_INCLUDE_ADMIN_UI=true to keep Admin UI static assets.
ARG HONUA_INCLUDE_ADMIN_UI=false

# Restore dependencies
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-musl-x64" ;; \
        arm64) RUNTIME_ID="linux-musl-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    export PROTOBUF_PROTOC=/usr/bin/protoc && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaIncludeAdminUi=$HONUA_INCLUDE_ADMIN_UI" && \
    dotnet restore src/Honua.Server/Honua.Server.csproj \
      --runtime "$RUNTIME_ID" \
      $EXTRA_MSBUILD_ARGS

# Copy source code
COPY . .

# Build application (disable AOT for default image)
ARG CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-musl-x64" ;; \
        arm64) RUNTIME_ID="linux-musl-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    export PROTOBUF_PROTOC=/usr/bin/protoc && \
    EXTRA_MSBUILD_ARGS="-p:RuntimeIdentifier=$RUNTIME_ID -p:HonuaIncludeAdminUi=$HONUA_INCLUDE_ADMIN_UI" && \
    dotnet publish src/Honua.Server/Honua.Server.csproj \
      --configuration "$CONFIGURATION" \
      --runtime "$RUNTIME_ID" \
      --self-contained false \
      --output /app \
      -p:PublishAot=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      $EXTRA_MSBUILD_ARGS && \
    rm -rf /app/BlazorDebugProxy && \
    if [ "$HONUA_INCLUDE_ADMIN_UI" != "true" ]; then rm -rf /app/wwwroot; fi && \
    find /app -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Security: Install runtime dependencies
RUN apk add --no-cache \
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

# Security: Create runtime directories with proper permissions
RUN mkdir -p /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chown -R 1001:1001 /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chmod 750 /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics

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
      org.opencontainers.image.description="Production-ready geospatial feature server" \
      org.opencontainers.image.licenses="Elastic-2.0"

# Runtime configuration
ARG HONUA_INCLUDE_ADMIN_UI=false
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    HONUA_SERVE_ADMIN_UI=${HONUA_INCLUDE_ADMIN_UI}

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget -q -T 5 -O /dev/null http://localhost:8080/healthz/live || exit 1

EXPOSE 8080/tcp

ENTRYPOINT ["dotnet", "Honua.Server.dll"]
