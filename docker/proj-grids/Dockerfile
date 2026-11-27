# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# Default Compose database image. Grid downloads happen only at build time.
# The default base is Honua's Compose database image (docker/compose-postgres).
# The standalone diagnostic composition (compose.yml) supplies its own PostGIS base.
ARG BASE_IMAGE=ghcr.io/honua-io/honua-server@sha256:1b5a7fbe802f8e0342370ace747b074fa7e0e02c2fd14c8e94ff44df06e1b7ee
FROM alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf AS grid-downloads

COPY grids.txt fetch-grids.sh /tmp/proj-grids/
RUN set -eux; \
    apk add --no-cache bash curl ca-certificates; \
    bash /tmp/proj-grids/fetch-grids.sh /tmp/proj-grids/grids.txt /grids

FROM ${BASE_IMAGE}

# The PROJ data directory inside the postgis/postgis image. PROJ searches this
# path (PROJ_DATA / PROJ_LIB) for grid files; dropping the .tif grids here makes
# them discoverable with NETWORK_ENABLED=OFF (no CDN access at runtime).
ARG PROJ_DATA_DIR=/usr/share/proj

COPY --from=grid-downloads /grids/ ${PROJ_DATA_DIR}/

# Keep PROJ offline-deterministic: never reach the CDN at runtime; only the baked
# grids are used. (The base image already defaults to this, set explicitly here.)
ENV PROJ_NETWORK=OFF
