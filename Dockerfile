# Multi-stage Dockerfile for Honua Server
# JIT build for maximum compatibility (AOT via docker/Dockerfile.aot)
# Enhanced security: minimal attack surface, non-root user, read-only filesystem

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Security: Create non-root build user
RUN addgroup -g 1001 -S builduser && \
    adduser -S builduser -G builduser -u 1001

# Copy solution and project files first for better layer caching
COPY Honua.sln Directory.Build.props .editorconfig ./
COPY src/Honua.Core/*.csproj src/Honua.Core/
COPY src/Honua.Postgres/*.csproj src/Honua.Postgres/
COPY src/Honua.ServiceDefaults/*.csproj src/Honua.ServiceDefaults/
COPY src/Honua.Admin/*.csproj src/Honua.Admin/
COPY src/Honua.Server/*.csproj src/Honua.Server/

# Restore dependencies
RUN dotnet restore src/Honua.Server/Honua.Server.csproj

# Copy source code
COPY . .

# Build application (disable AOT for default image)
ARG CONFIGURATION=Release
RUN dotnet publish src/Honua.Server/Honua.Server.csproj \
    --configuration "$CONFIGURATION" \
    --output /app \
    -p:PublishAot=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Security: Update packages and install runtime dependencies
RUN apk upgrade --no-cache && \
    apk add --no-cache \
    icu-libs \
    tzdata \
    curl \
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
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f --max-time 5 --connect-timeout 3 http://localhost:8080/healthz/live || exit 1

EXPOSE 8080/tcp

ENTRYPOINT ["dotnet", "Honua.Server.dll"]
