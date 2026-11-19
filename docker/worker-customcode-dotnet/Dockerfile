# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e
#
# Honua custom-code GP worker (DOTNET runtime) — Phase 2.
#
# The .NET sibling of docker/worker-customcode-python. This image is the SANDBOXED
# unit that runs inside a per-job AWS Batch container: it clones a user's GP tool at a
# PINNED git SHA, builds the user's .csproj against a WARM NuGet cache, runs it under a
# SCOPED Honua SDK client, and uploads the tool's output artifacts through the job-scoped
# Honua artifact broker without cloud credentials.
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
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea
# Declared before the first FROM so the runtime stage FROM can use it.
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.12.4@sha256:5828162cffed3af330034ae0c3ada30deb1cfdaecf37585f96ed0924f1d1dfb7
# The shipped SDK is copied from this untouched stage, so it is exactly the digest-pinned image's.
FROM ${DOTNET_SDK_IMAGE} AS sdk

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Copy the harness solution + build infra first for better layer caching.
COPY docker/worker-customcode-dotnet/harness/ ./harness/

WORKDIR /src/harness

# Locked mode does not fail for a project without a lock file, so refuse one here: every
# harness project (and the warm-cache project) must carry a committed packages.lock.json.
RUN for project in $(find . -name '*.csproj'); do \
      test -f "$(dirname "$project")/packages.lock.json" \
        || { echo "missing packages.lock.json for $project" >&2; exit 1; }; \
    done

# Publish the harness host (framework-dependent; NOT AOT — it loads user assemblies
# dynamically). NuGetAudit is off (offline restore from nuget.org only). The harness, sample and
# warm-cache restores are in locked mode: only package contents whose hashes are committed in
# the harness packages.lock.json files get in. The build-stage GDAL probe below reuses the
# packages RasterTool's locked restore just verified; it never ships.
RUN dotnet publish src/Honua.CustomCode.Harness/Honua.CustomCode.Harness.csproj \
      --configuration Release \
      -p:NuGetAudit=false \
      -p:RestoreLockedMode=true \
      --output /opt/harness

# IN-BUILD SANITY CHECK (correct-by-construction): restore + build the sample tools.
# This proves the SDK contract resolves, the geometry stack restores, and a real
# IGeoprocessingTool compiles — failing the image build early if anything regresses.
RUN dotnet build samples/BufferTool/BufferTool.csproj \
      --configuration Release \
      -p:NuGetAudit=false \
      -p:RestoreLockedMode=true \
      --output /tmp/sample-build \
 && test -f /tmp/sample-build/BufferTool.dll

# GDAL INTEROP SANITY CHECK: build the raster sample (which references the GDAL C#
# bindings MaxRev.Gdal.Core + MaxRev.Gdal.LinuxRuntime.Minimal) AND execute a tiny
# probe that calls GdalBase.ConfigureAll() and asserts a driver count > 0 — proving
# the self-contained native GDAL loads and OSGeo.GDAL interop works in-process, not
# just that it compiles. Fails the image build early on any binding/native regression.
RUN dotnet build samples/RasterTool/RasterTool.csproj \
      --configuration Release \
      -p:NuGetAudit=false \
      -p:RestoreLockedMode=true \
      --output /tmp/raster-build \
 && test -f /tmp/raster-build/RasterTool.dll
RUN mkdir -p /tmp/gdalprobe \
 && cat > /tmp/gdalprobe/Probe.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MaxRev.Gdal.Core" Version="3.12.4.520" />
    <PackageReference Include="MaxRev.Gdal.LinuxRuntime.Minimal" Version="3.12.4.520" />
  </ItemGroup>
</Project>
EOF
RUN cat > /tmp/gdalprobe/Program.cs <<EOF
using System;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;

GdalBase.ConfigureAll();
var drivers = Gdal.GetDriverCount();
Console.WriteLine($"GDAL {Gdal.VersionInfo("RELEASE_NAME")} drivers={drivers}");
if (drivers <= 0)
{
    Console.Error.WriteLine("FATAL: GDAL registered no drivers");
    return 1;
}
if (Gdal.GetDriverByName("GTiff") is null)
{
    Console.Error.WriteLine("FATAL: GTiff driver missing");
    return 1;
}
return 0;
EOF
RUN cd /tmp/gdalprobe && dotnet run -c Release -p:NuGetAudit=false

# --- runtime stage: GDAL-full base + the .NET SDK + git + the warm cache --------------
# Pinned GDAL "ubuntu-full" base (matches the python image's base family). It ships the
# full GDAL driver set; .NET tools that shell out to gdal* or P/Invoke GDAL get it here.
FROM ${GDAL_BASE_IMAGE} AS runtime

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

# git (clone user code) and ca-certificates (TLS to git host + Honua API). The .NET SDK
# is required at runtime to `dotnet build` the user's .csproj at job time (this is why the
# image carries the SDK, not just the runtime).
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      git \
 && rm -rf /var/lib/apt/lists/*

# The .NET SDK comes from the digest-pinned SDK image of the build stage: the same portable
# linux-x64 SDK the install script would download, but verified by the image digest and
# pinned to one version instead of the newest of a channel.
ENV DOTNET_ROOT=/usr/share/dotnet
COPY --from=sdk /usr/share/dotnet ${DOTNET_ROOT}
RUN ln -s "${DOTNET_ROOT}/dotnet" /usr/bin/dotnet \
 && dotnet --info > /dev/null

# The published harness host.
COPY --from=build /opt/harness /opt/harness

# Pre-warm the NuGet cache (NetTopologySuite + GeoJSON IO + the Honua .NET SDK + the GDAL
# bindings with the linux-x64 native asset) so the common user tool builds with NO network
# restore at job time. harness/warm is a restore-only project whose versions come from the
# harness Directory.Packages.props; it restores in locked mode from its committed lock file.
ENV NUGET_PACKAGES=/opt/nuget/packages
COPY docker/worker-customcode-dotnet/harness/Directory.Build.props \
     docker/worker-customcode-dotnet/harness/Directory.Packages.props \
     docker/worker-customcode-dotnet/harness/NuGet.config /tmp/warm/
COPY docker/worker-customcode-dotnet/harness/warm/ /tmp/warm/warm/
RUN dotnet restore /tmp/warm/warm/Honua.CustomCode.Warm.csproj -p:RestoreLockedMode=true \
 && rm -rf /tmp/warm

# Non-root user (uid 1001, matching docker/worker-gdal + the python image). The per-job
# scratch tree is owned by the worker so clone + build + output writes succeed.
RUN groupadd --gid 1001 --system honua \
 && useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin honua \
 && mkdir -p /work/src /work/build /work/out \
 && chown -R 1001:1001 /work /opt/nuget

WORKDIR /work

ENV HONUA_CUSTOMCODE_SDK_ASSEMBLY=/opt/harness/Honua.CustomCode.Sdk.dll \
    HONUA_CUSTOMCODE_SOURCE_ROOT=/work/src \
    HONUA_CUSTOMCODE_BUILD_OUTPUT=/work/build \
    HONUA_CUSTOMCODE_WORKDIR=/work/out \
    GDAL_NUM_THREADS=ALL_CPUS

LABEL org.opencontainers.image.title="honua-worker-customcode-dotnet" \
      org.opencontainers.image.description="Honua custom-code GP worker (.NET runtime): runs user IGeoprocessingTool implementations at a pinned git SHA under a scoped Honua SDK client." \
      org.opencontainers.image.licenses="LicenseRef-FCL-1.0-ALv2" \
      honua.runtime.profile="customcode-dotnet" \
      security.non-root="true" \
      security.user="1001"

COPY LICENSE THIRD-PARTY-NOTICES.md /usr/share/doc/honua/
USER 1001:1001

# The harness reads CUSTOMCODE_* / HONUA_* from the Batch container env (or a
# CUSTOMCODE_JOB_SPEC file). See harness/src/Honua.CustomCode.Harness/JobSpec.cs.
ENTRYPOINT ["dotnet", "/opt/harness/Honua.CustomCode.Harness.dll"]
