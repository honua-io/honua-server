# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

# Multi-stage build for Native AOT
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# Install native AOT build dependencies
RUN apk add --no-cache \
    clang21=21.1.2-r2 \
    build-base=0.5-r3 \
    zlib-dev=1.3.1-r2 \
    && rm -rf /var/cache/apk/*

# Copy project files first (for layer caching)
COPY Directory.Build.props .editorconfig ./
COPY src/Honua.Server/Honua.Server.csproj src/Honua.Server/
COPY src/Honua.Core/Honua.Core.csproj src/Honua.Core/
COPY src/Honua.Postgres/Honua.Postgres.csproj src/Honua.Postgres/
COPY src/Honua.ServiceDefaults/Honua.ServiceDefaults.csproj src/Honua.ServiceDefaults/
COPY src/Honua.Admin/Honua.Admin.csproj src/Honua.Admin/

# Restore packages (cached layer)
RUN dotnet restore src/Honua.Server/Honua.Server.csproj

# Copy source code
COPY . .

# Build and publish with Native AOT
ARG TARGETARCH
RUN case "${TARGETARCH:-amd64}" in \
        amd64) RUNTIME_ID="linux-musl-x64" ;; \
        arm64) RUNTIME_ID="linux-musl-arm64" ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" && exit 1 ;; \
    esac && \
    dotnet publish src/Honua.Server \
      --configuration Release \
      --runtime "$RUNTIME_ID" \
      --self-contained true \
      --output /app \
      --no-restore \
      -p:PublishAot=true \
      -p:StripSymbols=true \
      -p:OptimizationPreference=Speed \
      -p:InvariantGlobalization=true

# Production stage - minimal Alpine image with runtime dependencies
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS production

# Create non-root user for security
RUN addgroup -g 1001 -S honua && \
    adduser -u 1001 -S honua -G honua

# Install PostGIS client tools if needed
RUN apk add --no-cache \
    icu-libs \
    tzdata

WORKDIR /app

# Copy the published app
COPY --from=build /app .

# Create directories that need to be writable at runtime
RUN mkdir -p /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chown -R honua:honua /app /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chmod +x /app/Honua.Server

# Switch to non-root user
USER honua

# Security labels for enhanced container security
LABEL security.non-root="true"
LABEL security.capabilities.drop="ALL"
LABEL security.read-only-root="true"

# Configure health checks
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/healthz/live || exit 1

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# Entry point
ENTRYPOINT ["./Honua.Server"]
