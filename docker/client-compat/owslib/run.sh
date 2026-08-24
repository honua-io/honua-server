#!/usr/bin/env bash
# Runs the OWSLib canonical OGC client certification suite against the Compose
# `honua` service and writes one .cert.json envelope per protocol
# (ogc-features, wfs, wms, wmts) into /output for the baseline-diff step.
#
# Compose (docker/client-compat/compose.yml, `owslib` service) must mount
# ../../tests:/workspace/tests:ro and ./output/owslib:/output.
#
# NOTE: this script deliberately never aborts before pytest has finished.
# tests/python/owslib_client/conftest.py writes the envelopes at session
# teardown, so a `set -e` abort or an early `exit` on a failing case would
# destroy the very evidence the lane exists to produce. Failures are captured
# in the envelope (status=fail with the failure text) and surfaced through the
# recorded exit status at the very end, after the envelopes are on disk.
set -uo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_OWSLIB_SERVICE_ID:=test_service}"
: "${HONUA_OWSLIB_COLLECTION_ID:=0}"
: "${HONUA_OWSLIB_RASTER_SERVICE_ID:=browser_compat}"
: "${HONUA_OWSLIB_RASTER_LAYER_ID:=2000}"

cd /workspace

# The compose healthcheck already gates depends_on, but a few belt-and-braces
# retries cover slow first boots when the lane is run standalone.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

export HONUA_BASE_URL
export HONUA_OWSLIB_BASE_URL="${HONUA_BASE_URL}"
export HONUA_OWSLIB_SERVICE_ID
export HONUA_OWSLIB_COLLECTION_ID
export HONUA_OWSLIB_RASTER_SERVICE_ID
export HONUA_OWSLIB_RASTER_LAYER_ID
export HONUA_OWSLIB_OUTPUT_DIR=/output

# Receipt bindings. `fixture_revision` and `server_config_revision` are digested
# from the read-only tests/ mount, so they always resolve. `server_version` is
# resolved by the lane from the authenticated admin version endpoint.
# `server_commit` has no in-container source -- the tests/ mount carries no git
# metadata -- so the caller must supply it if the envelope is to carry the full
# tested SHA; without it the shared writer records "unknown".
export HONUA_OWSLIB_SERVER_COMMIT="${HONUA_OWSLIB_SERVER_COMMIT:-${HONUA_SERVER_COMMIT:-${GITHUB_SHA:-}}}"
if [[ -z "${HONUA_OWSLIB_SERVER_COMMIT}" ]]; then
    unset HONUA_OWSLIB_SERVER_COMMIT
    echo "note: HONUA_OWSLIB_SERVER_COMMIT is unset; the envelope will record server_commit=unknown."
fi

# --confcutdir stops conftest discovery at the lane package. tests/python/
# conftest.py (the shared integration-suite root conftest) pulls in
# testcontainers/geopandas/psycopg, none of which this lane needs or installs;
# without the cut, collection would fail on an unrelated import. Everything the
# lane does need lives in tests/python/shared, which is imported as a normal
# package because tests/python stays on sys.path.
pytest tests/python/owslib_client \
    --confcutdir=tests/python/owslib_client \
    --override-ini="addopts=" \
    -v \
    --tb=short
status=$?

echo "OWSLib lane complete (pytest exit ${status}); envelopes written to /output:"
ls -la /output || true

exit "${status}"
