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
# GDAL base ships gdalwarp / ogr2ogr / gdal_translate / gdalmdiminfo etc.
# NOTE: must be the `ubuntu-full` variant, not `ubuntu-small` — the small image
# omits the NetCDF/HDF5 drivers required by the multidimensional coverage
# reader (ADR-0039 Path B). Verified: gdalmdiminfo reports "not recognized as
# being in a supported file format" against NetCDF4 on the small image.
#
# PINNED to a concrete patch tag (not `-latest`) so the worker's driver universe —
# PROJ datum-shift grids, native FileGDB (OpenFileGDB), GML app-schema, netCDF /
# GRIB / HDF — is reproducible across builds. PROJ ships with this base; the
# native vector reproject (transform.reproject on the native profile) and the OGR
# import reader (source.ogr) depend on its transformation pipelines.
ARG GDAL_VERSION=3.13.1
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.13.1@sha256:aff1d5515aa0e9b50be34ab11d6c0c2cfabc23cdcb7a2e0bc5748101eedb3e4a
# Pinned .NET runtime version installed onto the GDAL (Ubuntu) base.
ARG DOTNET_RUNTIME_VERSION=10.0.10
# PDAL is built from its upstream release archive against the SAME GDAL/Ubuntu
# toolchain as the runtime. Ubuntu 26.04 does not publish PDAL, and cross-release
# UbuntuGIS packages are intentionally forbidden. Pin both release and upstream
# checksum so the native point-cloud supply chain is reproducible.
ARG PDAL_VERSION=2.10.2
ARG PDAL_SOURCE_SHA256=1fa8156b0394136e891462507466fb8ee9f6fac98d3fe07f17e1fcdb53dbefd1

# ---------------------------------------------------------------------------
# Build stage: restore + publish the worker (framework-dependent, no AOT).
# ---------------------------------------------------------------------------
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Copy solution and project files first for better layer caching.
COPY Honua.sln Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config .editorconfig ./
COPY scripts/docker/restore-dotnet-with-github-packages.sh scripts/docker/
COPY src/Honua.Analyzers/*.csproj src/Honua.Analyzers/
COPY src/Honua.Core.Abstractions/*.csproj src/Honua.Core.Abstractions/
COPY src/Honua.Core/*.csproj src/Honua.Core/
COPY src/Honua.Geometry/*.csproj src/Honua.Geometry/
COPY src/Honua.Hosting/*.csproj src/Honua.Hosting/
COPY src/Honua.Jobs/*.csproj src/Honua.Jobs/
COPY src/Honua.ServiceDefaults/*.csproj src/Honua.ServiceDefaults/
COPY src/Honua.Worker.Gdal/*.csproj src/Honua.Worker.Gdal/
COPY eng/ eng/

# Restore the worker project and keep the NuGet cache mounted for publish.
# The worker is framework-dependent and NOT AOT, so no RuntimeIdentifier is set.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    sh scripts/docker/restore-dotnet-with-github-packages.sh \
      src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj \
    || dotnet restore src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj

# Copy the full source tree and publish. Restore again in the same RUN and cache
# mount as publish: a cached earlier restore layer does not prove that a fresh
# builder's mutable cache mount contains every package referenced by its assets.
COPY . .

ARG CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    sh scripts/docker/restore-dotnet-with-github-packages.sh \
      src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj \
 && dotnet publish src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj \
      --configuration "${CONFIGURATION}" \
      --no-restore \
      --self-contained false \
      -p:PublishAot=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      --output /app \
 && find /app -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

# ---------------------------------------------------------------------------
# PDAL build stage: compile the pinned upstream source against the exact GDAL
# 3.13.1 / Ubuntu 26.04 toolchain used by the runtime stage. No Noble packages,
# third-party distro repositories, or mutable source branches cross this seam.
# ---------------------------------------------------------------------------
FROM ${GDAL_BASE_IMAGE} AS pdal-build

ARG PDAL_VERSION
ARG PDAL_SOURCE_SHA256

# hadolint ignore=DL3008,DL3015
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends upgrade \
 && apt-get install -y --no-install-recommends \
      build-essential \
      ca-certificates \
      cmake \
      curl \
      libboost-dev \
      libcurl4-openssl-dev \
      libeigen3-dev \
      libgeotiff-dev \
      libjsoncpp-dev \
      libtbb-dev \
      libxml2-dev \
      ninja-build \
 && curl -fsSL \
      "https://github.com/PDAL/PDAL/releases/download/${PDAL_VERSION}/PDAL-${PDAL_VERSION}-src.tar.gz" \
      -o /tmp/pdal.tar.gz \
 && echo "${PDAL_SOURCE_SHA256}  /tmp/pdal.tar.gz" | sha256sum -c - \
 && mkdir /tmp/pdal-source \
 && tar -xzf /tmp/pdal.tar.gz -C /tmp/pdal-source --strip-components=1 \
 && cmake -S /tmp/pdal-source -B /tmp/pdal-build -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_INSTALL_PREFIX=/opt/pdal \
      -DCMAKE_INSTALL_RPATH=/opt/pdal/lib \
      -DWITH_TESTS=OFF \
      -DWITH_GCS=OFF \
      -DWITH_BACKTRACE=OFF \
      -DBUILD_DOCS=OFF \
      -DBUILD_TOOLS_LASDUMP=OFF \
 && cmake --build /tmp/pdal-build --parallel 2 \
 && cmake --install /tmp/pdal-build \
 && test -x /opt/pdal/bin/pdal \
 && rm -rf /tmp/pdal.tar.gz /tmp/pdal-source /tmp/pdal-build /var/lib/apt/lists/*

# ---------------------------------------------------------------------------
# Runtime stage: GDAL base + .NET runtime + published worker.
# ---------------------------------------------------------------------------
FROM ${GDAL_BASE_IMAGE} AS native-tools

ARG GDAL_VERSION
ARG DOTNET_RUNTIME_VERSION
ARG PDAL_VERSION

# Install the .NET runtime onto the GDAL (Ubuntu) base via the dotnet-install
# script. The GDAL base already provides gdalwarp / ogr2ogr on PATH.
#
# PDAL (Point Data Abstraction Library) is added here for the point-cloud
# decompress + reproject path (#1854): the `pdal translate` CLI decompresses LAZ
# and Cloud-Optimized Point Cloud (COPC) sources (laz-perf), reprojects via PROJ,
# and writes uncompressed LAS that the managed tiler (LasPointCloudReader +
# PointCloudTilesetBuilder) turns into 3D Tiles. Like GDAL, PDAL lives ONLY in
# this image; the lean serving image stays point-cloud-native-dep-free.
# hadolint ignore=DL3008,DL3015
# python3-scipy backs the custom Euclidean allocation worker step
# (proximity.euclidean-allocation, #2255): Scripts/gdal_euclidean_allocation.py
# uses SciPy's exact distance transform (nearest-feature index return) plus the
# GDAL Python bindings (shipped in the ubuntu-full base) since stock
# gdal_proximity has no nearest-source allocation mode.
# The OSGeo base also carries Canonical's Pebble service manager for image-level
# health checks. Honua runs the worker directly and does not use Pebble, so keep
# that unrelated Go binary (and its independent vulnerability surface) out of
# the final runtime image.
# hadolint ignore=DL3008,DL3015
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends upgrade \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      libcurl4t64 \
      libgeotiff5 \
      libicu-dev \
      libjsoncpp26 \
      libtbb12 \
      libxml2-16 \
      python3-scipy \
 && rm -f /usr/bin/pebble \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --version "${DOTNET_RUNTIME_VERSION}" --runtime aspnetcore --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm -f /tmp/dotnet-install.sh

COPY --from=pdal-build /opt/pdal /opt/pdal

ENV PATH="/opt/pdal/bin:${PATH}" \
    LD_LIBRARY_PATH="/opt/pdal/lib"

# Sanity check: the GDAL tools the worker shells out to must be present, and the
# NetCDF + GRIB drivers (multidimensional coverage convert-to-Zarr path) must be
# compiled in. The ubuntu-full base ships both; this is a build-time guard.
# `pdal` must also be on PATH with the LAS reader + reprojection filter compiled
# in (LAZ/COPC decompress + reproject, #1854).
RUN command -v gdalwarp && command -v ogr2ogr && command -v gdalmdiminfo \
 && gdalinfo --version | grep -F "GDAL ${GDAL_VERSION}" \
 # gdal_grid backs the raster IDW interpolation tool (raster.interpolate-idw, #2141);
 # gdalwarp backs raster.resample and raster.mosaic. Both ship in the ubuntu-full base.
 && command -v gdal_grid \
 # Raster analysis & terrain GP tool pack (#2239 / #2240). gdal_calc.py backs
 # raster.map-algebra / raster.spectral-index / raster.reclassify; gdal_proximity.py
 # backs proximity.euclidean-distance; gdal_polygonize.py backs conversion.polygonize
 # (the GDAL Python utilities, shipped in the ubuntu-full base's python3-gdal layer).
 # gdal_contour / gdal_viewshed / gdal_rasterize are C binaries backing
 # surface.contour / surface.viewshed / conversion.rasterize.
 && command -v gdal_calc.py \
 && command -v gdal_proximity.py \
 # The Euclidean allocation custom step (proximity.euclidean-allocation, #2255)
 # imports the GDAL Python bindings + NumPy + SciPy at runtime; fail the build
 # early if any are missing from the image.
 && python3 -c "import numpy, scipy; from scipy import ndimage; from osgeo import gdal" \
 && command -v gdal_polygonize.py \
 && command -v gdal_contour \
 && command -v gdal_viewshed \
 && command -v gdal_rasterize \
 && gdalinfo --formats | grep -qi netCDF \
 && gdalinfo --formats | grep -qi GRIB \
 && command -v pdal \
 && pdal --version | grep -F "pdal ${PDAL_VERSION}" \
 && pdal --drivers | grep -qi readers.las \
 && pdal --drivers | grep -qi filters.reprojection \
 && ! ldd "$(command -v pdal)" | grep -q 'not found' \
 && ! ldd "$(readlink -f /opt/pdal/lib/libpdalcpp.so)" | grep -q 'not found' \
 && dotnet --list-runtimes | grep -F "Microsoft.AspNetCore.App ${DOTNET_RUNTIME_VERSION}" \
 && dotnet --list-runtimes | grep -F "Microsoft.NETCore.App ${DOTNET_RUNTIME_VERSION}" \
 && ! command -v pebble \
 # PROJ datum-shift support for native vector reproject (transform.reproject on
 # the native profile): projsync/proj.db must be present so PROJ can apply
 # datum/grid shifts (e.g. NAD 27 -> WGS 84). The full base ships PROJ.
 && command -v projinfo \
 # OGR import-reader (source.ogr) format breadth the managed readers cannot serve.
 && ogr2ogr --formats | grep -qi OpenFileGDB \
 && ogr2ogr --formats | grep -qi GML

# Keep the independently testable native-tools stage free of application bits;
# the final worker stage adds only the published managed host and runtime user.
FROM native-tools AS runtime

ARG GDAL_VERSION
ARG DOTNET_RUNTIME_VERSION
ARG PDAL_VERSION

# Non-root runtime user.
RUN groupadd --gid 1001 --system honua \
 && useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin honua \
 && mkdir -p /app /tmp/honua-gdal-worker \
 && chown -R 1001:1001 /app /tmp/honua-gdal-worker

WORKDIR /app
COPY --from=build --chown=1001:1001 /app .

# Guard: the custom worker steps (e.g. the Euclidean allocation step, #2255) must
# be published alongside the worker assemblies so the executors can invoke them.
RUN test -f /app/Scripts/gdal_euclidean_allocation.py

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    GdalWorker__ScratchRoot=/tmp/honua-gdal-worker

LABEL org.opencontainers.image.title="honua-worker-etl" \
      org.opencontainers.image.description="Honua heavyweight GP/ETL worker (GDAL + PDAL equipped, native runtime profile)" \
      org.opencontainers.image.licenses="Elastic-2.0" \
      honua.runtime.profile="native" \
      honua.native.gdal.version="${GDAL_VERSION}" \
      honua.native.pdal.version="${PDAL_VERSION}" \
      honua.runtime.dotnet.version="${DOTNET_RUNTIME_VERSION}" \
      security.non-root="true" \
      security.user="1001"

USER 1001:1001

ENTRYPOINT ["dotnet", "Honua.Worker.Gdal.dll"]
