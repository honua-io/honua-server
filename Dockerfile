# Multi-stage Dockerfile for Honua Server
# Native AOT build for minimal image size and fast cold start

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Install native AOT build dependencies
RUN apk add --no-cache \
    clang \
    build-base \
    zlib-dev

# Copy solution and project files first for better layer caching
COPY Honua.sln Directory.Build.props .editorconfig ./
COPY src/Honua.Core/*.csproj src/Honua.Core/
COPY src/Honua.Postgres/*.csproj src/Honua.Postgres/
COPY src/Honua.Server/*.csproj src/Honua.Server/

# Restore dependencies
RUN dotnet restore src/Honua.Server/Honua.Server.csproj

# Copy source code
COPY . .

# Build Native AOT application for minimal image size
ARG CONFIGURATION=Release
RUN dotnet publish src/Honua.Server/Honua.Server.csproj \
    --configuration $CONFIGURATION \
    --runtime linux-musl-x64 \
    --self-contained true \
    --output /app \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:OptimizationPreference=Speed \
    -p:IlcOptimizationPreference=Speed

# Runtime stage - use runtime-deps for AOT (no .NET runtime needed)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime

# Install required packages for PostGIS connectivity
RUN apk add --no-cache \
    icu-libs \
    tzdata

# Create non-root user for security
RUN addgroup -g 1001 -S honua && \
    adduser -S honua -G honua -u 1001

WORKDIR /app
COPY --from=build /app .

# Create directories that need to be writable at runtime
RUN mkdir -p /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chown -R honua:honua /app /tmp/honua-logs /tmp/honua-cache /tmp/dotnet-diagnostics && \
    chmod 755 /app/Honua.Server

# Switch to non-root user
USER honua

# Security labels for enhanced container security
LABEL security.non-root="true"
LABEL security.capabilities.drop="ALL"
LABEL security.read-only-root="true"

# Configure ASP.NET Core
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Health check endpoint
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/healthz/live || exit 1

EXPOSE 8080

ENTRYPOINT ["./Honua.Server"]