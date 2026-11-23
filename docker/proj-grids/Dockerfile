# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# Default Compose database image. Grid downloads happen only at build time.
# The default base is Honua's Compose database image (docker/compose-postgres).
# The standalone diagnostic composition (compose.yml) supplies its own PostGIS base.
ARG BASE_IMAGE=ghcr.io/honua-io/honua-server@sha256:2d215e136348f3e0d7139c352f88281b5f980d19d78a00d3d308b56fa0060fb4
FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc AS grid-downloads

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
