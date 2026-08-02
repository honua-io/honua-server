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
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.13.1
# Pinned .NET runtime version installed onto the GDAL (Ubuntu) base.
ARG DOTNET_RUNTIME_VERSION=10.0
# Ubuntu Noble does not publish PDAL in its standard archive. UbuntuGIS publishes
# the Noble build used here; pin both the package and repository signing key so a
# mutable PPA cannot silently change the worker's point-cloud runtime.
ARG PDAL_VERSION=2.6.2+ds-1~noble2
ARG UBUNTUGIS_KEY_FINGERPRINT=2EC86B48E6A9F326623CD22FFF0E7BBEC491C6A1

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
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=secret,id=github_actor \
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
FROM ${GDAL_BASE_IMAGE} AS native-tools

ARG DOTNET_RUNTIME_VERSION
ARG PDAL_VERSION
ARG UBUNTUGIS_KEY_FINGERPRINT

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
# Noble has no PDAL package in the Ubuntu archive. Install the exact UbuntuGIS
# Noble build through a dedicated signed-by keyring, after checking the complete
# key fingerprint. Removing the source/key after installation prevents later
# layers from accidentally upgrading the native stack from the PPA.
# hadolint ignore=DL3008,DL3015
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg libicu-dev libssl3t64 openssl python3-scipy \
 && curl -fsSL "https://keyserver.ubuntu.com/pks/lookup?op=get&search=0x${UBUNTUGIS_KEY_FINGERPRINT}" -o /tmp/ubuntugis.asc \
 && test "$(gpg --batch --show-keys --with-colons /tmp/ubuntugis.asc | awk -F: '$1 == "fpr" { print $10; exit }')" = "${UBUNTUGIS_KEY_FINGERPRINT}" \
 && gpg --batch --dearmor --output /usr/share/keyrings/ubuntugis-archive-keyring.gpg /tmp/ubuntugis.asc \
 && echo "deb [signed-by=/usr/share/keyrings/ubuntugis-archive-keyring.gpg] https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu noble main" > /etc/apt/sources.list.d/ubuntugis.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      libpdal-plugins="${PDAL_VERSION}" \
      pdal="${PDAL_VERSION}" \
 && rm -rf /var/lib/apt/lists/* \
 && rm -f /etc/apt/sources.list.d/ubuntugis.list /usr/share/keyrings/ubuntugis-archive-keyring.gpg /tmp/ubuntugis.asc \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel "${DOTNET_RUNTIME_VERSION}" --runtime aspnetcore --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm -f /tmp/dotnet-install.sh

# Sanity check: the GDAL tools the worker shells out to must be present, and the
# NetCDF + GRIB drivers (multidimensional coverage convert-to-Zarr path) must be
# compiled in. The ubuntu-full base ships both; this is a build-time guard.
# `pdal` must also be on PATH with the LAS reader + reprojection filter compiled
# in (LAZ/COPC decompress + reproject, #1854).
RUN command -v gdalwarp && command -v ogr2ogr && command -v gdalmdiminfo \
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
 && pdal --drivers | grep -qi readers.las \
 && pdal --drivers | grep -qi filters.reprojection \
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
      security.non-root="true" \
      security.user="1001"

USER 1001:1001

ENTRYPOINT ["dotnet", "Honua.Worker.Gdal.dll"]
