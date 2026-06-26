# syntax=docker/dockerfile:1.7
#
# Honua custom-code GP worker (PYTHON runtime) — Phase 1.
#
# This image is the SANDBOXED unit that runs inside a per-job AWS Batch
# container. It is SEPARATE from docker/worker-gdal (that image is the .NET
# native ETL worker and ships no python/git/pip). Here we want the OPPOSITE:
# a Python runtime with the GDAL python bindings + a geospatial stack +
# honua-sdk so user-authored GP tools run with the common geo libraries already
# present (no per-job restore for the common case).
#
# Base: GDAL "ubuntu-full" so the GDAL python bindings (osgeo.gdal/ogr/osr) and
# the full driver set are present. Pinned to an explicit GDAL release tag — do
# NOT use ``-latest`` (reproducibility + supply-chain).
#
# The ENTRYPOINT is the harness (honua_customcode_harness), which clones user
# code at a pinned git SHA, restores extra deps, builds a SCOPED HonuaClient,
# STRIPS the IMDS/credential + token env vars, runs the tool's execute(context),
# and uploads artifacts. See ./harness/.

# Pinned GDAL "full" base — ships python3 + osgeo bindings + the full driver set.
# Tag verified available on ghcr.io/osgeo/gdal (ubuntu-full-<gdal-version>).
ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.12.4

FROM ${GDAL_BASE_IMAGE} AS runtime

# Pin the SDK + geo stack so the image is reproducible and matches
# harness/pyproject.toml (honua-sdk==0.1.4).
ARG HONUA_SDK_VERSION=0.1.4

ENV DEBIAN_FRONTEND=noninteractive \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

# git (clone user code), pip, ca-certificates (TLS to the git host + Honua API).
# The GDAL ubuntu-full base already provides python3 + the osgeo bindings; we add
# pip/venv/git on top. Build toolchain is included so a tool's deps_manifest can
# compile sdist-only wheels at job time.
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      git \
      python3-pip \
      python3-venv \
      python3-dev \
      build-essential \
 && rm -rf /var/lib/apt/lists/*

# Pre-install the geospatial stack + the Honua SDK into the system interpreter so
# the common tool needs no per-job restore. ``--break-system-packages`` is
# required on recent Debian/Ubuntu (PEP 668) to install into the system env of
# the GDAL base image. rasterio/fiona link against the base GDAL.
# hadolint ignore=DL3013
RUN python3 -m pip install --no-cache-dir --break-system-packages \
      "rasterio>=1.3" \
      "geopandas>=0.14" \
      "shapely>=2.0" \
      "pyproj>=3.6" \
      "fiona>=1.9" \
      "boto3>=1.34" \
      "honua-sdk==${HONUA_SDK_VERSION}"

# Install the harness package itself (the ENTRYPOINT). Copying only the harness
# subtree keeps the build context tight and the layer cache stable.
COPY docker/worker-customcode-python/harness /opt/honua-customcode-harness
# hadolint ignore=DL3013
RUN python3 -m pip install --no-cache-dir --break-system-packages \
      /opt/honua-customcode-harness

# Correctness check: the SDK + geo bindings + the harness must import, and the
# console script must be on PATH. Fails the build early if a wheel didn't link.
RUN python3 -c "import honua_sdk, rasterio, geopandas, shapely, pyproj, fiona, boto3; import osgeo.gdal" \
 && python3 -c "import honua_customcode_harness as h; assert hasattr(h, 'run')" \
 && command -v honua-customcode-harness

# Non-root user (uid 1001, matching docker/worker-gdal). The per-job scratch
# tree is owned by the worker so clone + output writes succeed without root.
RUN groupadd --gid 1001 --system honua \
 && useradd --uid 1001 --gid 1001 --system --no-create-home --shell /usr/sbin/nologin honua \
 && mkdir -p /work/src /work/out \
 && chown -R 1001:1001 /work

WORKDIR /work

ENV HONUA_CUSTOMCODE_SOURCE_ROOT=/work/src \
    HONUA_CUSTOMCODE_WORKDIR=/work/out \
    # Refuse to run user code as root even if the orchestrator overrides USER.
    GDAL_NUM_THREADS=ALL_CPUS

LABEL org.opencontainers.image.title="honua-worker-customcode-python" \
      org.opencontainers.image.description="Honua custom-code GP worker (Python runtime): runs user GP tools at a pinned git SHA under a scoped Honua SDK client." \
      org.opencontainers.image.licenses="Elastic-2.0" \
      honua.runtime.profile="customcode-python" \
      security.non-root="true" \
      security.user="1001"

USER 1001:1001

# The harness console script reads CUSTOMCODE_* / HONUA_* from the Batch
# container env (or a CUSTOMCODE_JOB_SPEC file). See harness/honua_customcode_harness/jobspec.py.
ENTRYPOINT ["honua-customcode-harness"]
