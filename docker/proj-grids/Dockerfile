# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# Default Compose database image. Grid downloads happen only at build time.
# Compose and the integration fixture use the existing PostgreSQL 17 / pgRouting
# base. The standalone diagnostic composition supplies its pinned PostGIS base.
ARG BASE_IMAGE=postgis/postgis:18-3.6@sha256:60f6ad1d21ea86a67d47780b9a0d1e1d200500f62b19293fa834d0dea80b8677
FROM debian:bookworm-slim AS grid-downloads

COPY grids.txt fetch-grids.sh /tmp/proj-grids/
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl ca-certificates; \
    bash /tmp/proj-grids/fetch-grids.sh /tmp/proj-grids/grids.txt /grids; \
    rm -rf /var/lib/apt/lists/*

FROM ${BASE_IMAGE}

# The PROJ data directory inside the postgis/postgis image. PROJ searches this
# path (PROJ_DATA / PROJ_LIB) for grid files; dropping the .tif grids here makes
# them discoverable with NETWORK_ENABLED=OFF (no CDN access at runtime).
ARG PROJ_DATA_DIR=/usr/share/proj

COPY --from=grid-downloads /grids/ ${PROJ_DATA_DIR}/

# Keep PROJ offline-deterministic: never reach the CDN at runtime; only the baked
# grids are used. (The base image already defaults to this, set explicitly here.)
ENV PROJ_NETWORK=OFF
