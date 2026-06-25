# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
#
# Honua datum-fidelity PostGIS image (issue #1501).
#
# Extends the standard Honua test/runtime PostGIS image with the canonical PROJ
# NADCON / NADCON5 / NTv2 / GEOID grid files (see grids.txt). The base
# postgis/postgis:* images ship only the minimal Debian libproj-data subset
# (legacy NTv1 nad27/nad83 tables), so grid-gated Esri-default datum
# transformations either fall back to a lower-accuracy bundled operation or fail
# the explicit-grid contract. Baking the grids into the PROJ data directory lets
# the runtime PROJ resolve those operations at full accuracy, so the shared
# DatumTransformSql chokepoint (query + import reprojection paths) produces the
# grid-based shift instead of an approximation.
#
# This image is ISOLATED and OPT-IN: nothing in the default build/test/CITE path
# pulls it. It is used by the gated grid-fidelity integration test and by local
# provisioning (see compose.yml / README.md). The image tag this produces may be
# wired into integration/CITE compositions in a follow-up; the base PostGIS tag
# used elsewhere is unchanged.
#
# Pin the base to the same tag the Testcontainer fixture uses
# (tests/dotnet/Honua.TestKit/PostgresFixture.cs) so PROJ behavior matches.
ARG BASE_IMAGE=postgis/postgis:18-3.6
FROM ${BASE_IMAGE}

# The PROJ data directory inside the postgis/postgis image. PROJ searches this
# path (PROJ_DATA / PROJ_LIB) for grid files; dropping the .tif grids here makes
# them discoverable with NETWORK_ENABLED=OFF (no CDN access at runtime).
ARG PROJ_DATA_DIR=/usr/share/proj

COPY grids.txt /tmp/proj-grids/grids.txt
COPY fetch-grids.sh /tmp/proj-grids/fetch-grids.sh

# curl is needed only at build time to fetch the grids; remove it and the apt
# lists afterwards to keep the image lean and avoid an unused runtime dependency.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl ca-certificates; \
    chmod +x /tmp/proj-grids/fetch-grids.sh; \
    /tmp/proj-grids/fetch-grids.sh /tmp/proj-grids/grids.txt "${PROJ_DATA_DIR}"; \
    apt-get purge -y --auto-remove curl; \
    rm -rf /var/lib/apt/lists/* /tmp/proj-grids

# Keep PROJ offline-deterministic: never reach the CDN at runtime; only the baked
# grids are used. (The base image already defaults to this, set explicitly here.)
ENV PROJ_NETWORK=OFF
