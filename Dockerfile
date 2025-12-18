# Multi-stage Dockerfile for Honua Server
# Supports both JIT and AOT builds

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy all source code
COPY . .

# Build application (JIT for Phase 0 - AOT requires additional native dependencies)
ARG CONFIGURATION=Release
RUN dotnet publish src/Honua.Server/Honua.Server.csproj \
    --configuration $CONFIGURATION \
    --runtime linux-musl-x64 \
    --self-contained false \
    --output /app \
    -p:PublishAot=false

# Runtime stage - use Alpine for minimal size
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Install required packages for PostGIS connectivity
RUN apk add --no-cache \
    icu-libs \
    tzdata

# Create non-root user for security
RUN addgroup -g 1001 -S honua && \
    adduser -S honua -G honua -u 1001

WORKDIR /app
COPY --from=build /app .

# Change ownership to non-root user
RUN chown -R honua:honua /app
USER honua

# Configure ASP.NET Core
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Health check endpoint
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/healthz/live || exit 1

EXPOSE 8080

ENTRYPOINT ["dotnet", "Honua.Server.dll"]