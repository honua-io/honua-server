# syntax=docker/dockerfile:1.7
#
# Honua heavyweight GP/ETL worker image (honua-worker-etl) — ADR-0038.
#
# This image is the OPTIONAL, native-profile counterpart to the lean serving
# image (/Dockerfile). It layers the .NET runtime onto a GDAL-equipped base so
# the worker can shell out to gdalwarp / ogr2ogr / etc. It runs the SAME durable
# job-execution loop as the serving image (JobExecutionService), but registers
# only native-profile executors, so it claims ONLY jobs whose RuntimeProfile is
# "native".
#
# GUARDRAIL: GDAL lives ONLY in this image. The default serving image
# (/Dockerfile) and Honua.Server.csproj stay GDAL-free; this Dockerfile does not
# build or modify them.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
# GDAL base ships gdalwarp / ogr2ogr / gdal_translate etc. Matches the image
# family already used by docker/client-compat/gdal.
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-small-latest
# Pinned .NET runtime version installed onto the GDAL (Ubuntu) base.
ARG DOTNET_RUNTIME_VERSION=10.0

# ---------------------------------------------------------------------------
# Build stage: restore + publish the worker (framework-dependent, no AOT).
# ---------------------------------------------------------------------------
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Copy solution and project files first for better layer caching.
COPY Honua.sln Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config .editorconfig ./
COPY scripts/docker/restore-dotnet-with-github-packages.sh scripts/docker/
COPY src/Honua.Core/*.csproj src/Honua.Core/
COPY src/Honua.DuckDB/*.csproj src/Honua.DuckDB/
COPY src/Honua.MySql/*.csproj src/Honua.MySql/
COPY src/Honua.Postgres/*.csproj src/Honua.Postgres/
COPY src/Honua.SqlServer/*.csproj src/Honua.SqlServer/
COPY src/Honua.ServiceDefaults/*.csproj src/Honua.ServiceDefaults/
COPY src/Honua.Server/*.csproj src/Honua.Server/
COPY src/Honua.Worker.Gdal/*.csproj src/Honua.Worker.Gdal/

# Restore the worker project (pulls Honua.Server + Honua.Core transitively).
# The worker is framework-dependent and NOT AOT, so no RuntimeIdentifier is set.
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    sh scripts/docker/restore-dotnet-with-github-packages.sh \
      src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj \
    || dotnet restore src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj

# Copy the full source tree and publish.
COPY . .

ARG CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj \
      --configuration "${CONFIGURATION}" \
      --no-restore \
      --self-contained false \
      -p:PublishAot=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      --output /app \
 && find /app -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

# ---------------------------------------------------------------------------
# Runtime stage: GDAL base + .NET runtime + published worker.
# ---------------------------------------------------------------------------
FROM ${GDAL_BASE_IMAGE} AS runtime

ARG DOTNET_RUNTIME_VERSION

# Install the .NET runtime onto the GDAL (Ubuntu) base via the dotnet-install
# script. The GDAL base already provides gdalwarp / ogr2ogr on PATH.
# hadolint ignore=DL3008,DL3015
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu-dev \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel "${DOTNET_RUNTIME_VERSION}" --runtime dotnet --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm -f /tmp/dotnet-install.sh

# Sanity check: the GDAL tools the worker shells out to must be present.
RUN command -v gdalwarp && command -v ogr2ogr

# Non-root runtime user.
RUN groupadd --gid 1001 --system honua \
 && useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin honua \
 && mkdir -p /app /tmp/honua-gdal-worker \
 && chown -R 1001:1001 /app /tmp/honua-gdal-worker

WORKDIR /app
COPY --from=build --chown=1001:1001 /app .

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    GdalWorker__ScratchRoot=/tmp/honua-gdal-worker

LABEL org.opencontainers.image.title="honua-worker-etl" \
      org.opencontainers.image.description="Honua heavyweight GP/ETL worker (GDAL-equipped, native runtime profile)" \
      org.opencontainers.image.licenses="Elastic-2.0" \
      honua.runtime.profile="native" \
      security.non-root="true" \
      security.user="1001"

USER 1001:1001

ENTRYPOINT ["dotnet", "Honua.Worker.Gdal.dll"]
