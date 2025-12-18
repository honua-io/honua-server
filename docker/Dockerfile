# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

# Multi-stage build for Native AOT
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# Copy solution and project files first (for layer caching)
COPY *.sln ./
COPY src/Honua.Server/Honua.Server.csproj src/Honua.Server/
COPY src/Honua.Core/Honua.Core.csproj src/Honua.Core/
COPY src/Honua.Postgres/Honua.Postgres.csproj src/Honua.Postgres/
COPY tests/Honua.TestKit/Honua.TestKit.csproj tests/Honua.TestKit/
COPY tests/Honua.Server.Tests/Honua.Server.Tests.csproj tests/Honua.Server.Tests/
COPY tests/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj tests/Honua.Architecture.Tests/

# Restore packages (cached layer)
RUN dotnet restore Honua.sln

# Copy source code
COPY . .

# Build and publish with Native AOT
RUN dotnet publish src/Honua.Server \
    --configuration Release \
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

# Set ownership and permissions
RUN chown -R honua:honua /app && \
    chmod +x /app/Honua.Server

# Switch to non-root user
USER honua

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