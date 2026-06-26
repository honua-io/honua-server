# syntax=docker/dockerfile:1.7
#
# Honua custom-code GP worker (DOTNET runtime) — Phase 2.
#
# The .NET sibling of docker/worker-customcode-python. This image is the SANDBOXED
# unit that runs inside a per-job AWS Batch container: it clones a user's GP tool at a
# PINNED git SHA, builds the user's .csproj against a WARM NuGet cache, runs it under a
# SCOPED Honua SDK client, and uploads the tool's output artifacts to the job's S3
# output prefix.
#
# It is SEPARATE from docker/worker-gdal (the .NET native ETL worker, which runs the
# durable job loop and ships no git). Here we want a .NET SDK (needed to `dotnet build`
# the user's project at job time) on a GDAL-full base, plus git/ca-certs, with the
# common .NET geo deps (NetTopologySuite) + the Honua .NET SDK pre-warmed in the NuGet
# cache so the common tool needs no network restore.
#
# The ENTRYPOINT is the harness (Honua.CustomCode.Harness), which:
#   clone pinned SHA -> verify HEAD == SHA -> dotnet build the user .csproj ->
#   build a SCOPED Honua client -> STRIP the IMDS/credential + token env vars ->
#   activate the user's IGeoprocessingTool -> ExecuteAsync(context) -> upload artifacts.
# See ./harness/.

# --- build stage: publish the harness + warm the NuGet cache + sanity-build sample ---
# Pinned .NET SDK (do NOT use a floating tag).
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Copy the harness solution + build infra first for better layer caching.
COPY docker/worker-customcode-dotnet/harness/ ./harness/

WORKDIR /src/harness

# Publish the harness host (framework-dependent; NOT AOT — it loads user assemblies
# dynamically). NuGetAudit is off (offline restore from nuget.org only).
RUN dotnet publish src/Honua.CustomCode.Harness/Honua.CustomCode.Harness.csproj \
      --configuration Release \
      -p:NuGetAudit=false \
      --output /opt/harness

# IN-BUILD SANITY CHECK (correct-by-construction): restore + build the sample tool.
# This proves the SDK contract resolves, the geometry stack restores, and a real
# IGeoprocessingTool compiles — failing the image build early if anything regresses.
RUN dotnet build samples/BufferTool/BufferTool.csproj \
      --configuration Release \
      -p:NuGetAudit=false \
      --output /tmp/sample-build \
 && test -f /tmp/sample-build/BufferTool.dll

# --- runtime stage: GDAL-full base + the .NET SDK + git + the warm cache --------------
# Pinned GDAL "ubuntu-full" base (matches the python image's base family). It ships the
# full GDAL driver set; .NET tools that shell out to gdal* or P/Invoke GDAL get it here.
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.12.4
FROM ${GDAL_BASE_IMAGE} AS runtime

ARG DOTNET_SDK_CHANNEL=10.0
# Pin the geo + SDK package versions so the warm cache matches the harness solution.
ARG NETTOPOLOGYSUITE_VERSION=2.6.0
ARG HONUA_SDK_DOTNET_VERSION=0.1.4

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

# git (clone user code), ca-certificates (TLS to git host + Honua API), and the deps the
# dotnet-install script needs. The .NET SDK is required at runtime to `dotnet build` the
# user's .csproj at job time (this is why we install the SDK, not just the runtime).
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      git \
 && rm -rf /var/lib/apt/lists/*

# Install the pinned .NET SDK onto the GDAL (Ubuntu) base via the official script.
ENV DOTNET_ROOT=/usr/share/dotnet
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel "${DOTNET_SDK_CHANNEL}" --install-dir "${DOTNET_ROOT}" \
 && ln -s "${DOTNET_ROOT}/dotnet" /usr/bin/dotnet \
 && rm -f /tmp/dotnet-install.sh

# The published harness host.
COPY --from=build /opt/harness /opt/harness

# Pre-warm the NuGet cache (NetTopologySuite + GeoJSON IO + the Honua .NET SDK) so the
# common user tool builds with NO network restore at job time. We do this by restoring a
# tiny throwaway project that references the common packages into the shared global
# packages folder, then deleting the project (the populated cache layer is what we keep).
ENV NUGET_PACKAGES=/opt/nuget/packages
RUN mkdir -p /tmp/warm \
 && cat > /tmp/warm/warm.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NetTopologySuite" Version="${NETTOPOLOGYSUITE_VERSION}" />
    <PackageReference Include="NetTopologySuite.IO.GeoJSON" Version="4.0.0" />
    <PackageReference Include="Honua.Sdk" Version="${HONUA_SDK_DOTNET_VERSION}" />
  </ItemGroup>
</Project>
EOF
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    sh -c 'cd /tmp/warm && dotnet restore -p:NuGetAudit=false || true' \
 && rm -rf /tmp/warm

# Non-root user (uid 1001, matching docker/worker-gdal + the python image). The per-job
# scratch tree is owned by the worker so clone + build + output writes succeed.
RUN groupadd --gid 1001 --system honua \
 && useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin honua \
 && mkdir -p /work/src /work/build /work/out \
 && chown -R 1001:1001 /work /opt/nuget

WORKDIR /work

ENV HONUA_CUSTOMCODE_SOURCE_ROOT=/work/src \
    HONUA_CUSTOMCODE_BUILD_OUTPUT=/work/build \
    HONUA_CUSTOMCODE_WORKDIR=/work/out \
    GDAL_NUM_THREADS=ALL_CPUS

LABEL org.opencontainers.image.title="honua-worker-customcode-dotnet" \
      org.opencontainers.image.description="Honua custom-code GP worker (.NET runtime): runs user IGeoprocessingTool implementations at a pinned git SHA under a scoped Honua SDK client." \
      org.opencontainers.image.licenses="Elastic-2.0" \
      honua.runtime.profile="customcode-dotnet" \
      security.non-root="true" \
      security.user="1001"

USER 1001:1001

# The harness reads CUSTOMCODE_* / HONUA_* from the Batch container env (or a
# CUSTOMCODE_JOB_SPEC file). See harness/src/Honua.CustomCode.Harness/JobSpec.cs.
ENTRYPOINT ["dotnet", "/opt/harness/Honua.CustomCode.Harness.dll"]
