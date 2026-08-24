#!/usr/bin/env bash
# Runs the DuckDB Spatial certification lane against the Compose honua service
# and writes the .cert.json envelope to /output for the baseline-diff step.
#
# Required mounts (docker/client-compat/compose.yml):
#   ../../tests:/workspace/tests:ro
#   ./output/duckdb:/output
set -uo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
# Matches HONUA_ADMIN_PASSWORD in docker/client-compat/compose.yml. Used only to
# read the server version for the evidence receipt; the certification cases take
# the key from tests/python/shared/canonical_fixture.py.
: "${HONUA_ADMIN_API_KEY:=ClientCompatAdmin123!}"

cd /workspace

# The compose healthcheck already gates depends_on, but a few belt-and-braces
# retries cover slow first boots of the non-AOT image.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/python/duckdb_client/conftest.py honours HONUA_DUCKDB_OUTPUT_DIR and
# writes the envelope there directly, so the lane never writes into the
# read-only tests/ bind mount.
export HONUA_DUCKDB_BASE_URL="${HONUA_BASE_URL}"
export HONUA_DUCKDB_OUTPUT_DIR=/output

# /api/v1/admin/version is behind the admin API key, so the shared
# `read_server_version` helper's anonymous probe records "unknown". Resolve it
# here with the key and hand it to the collector, so the envelope's
# server_version receipt names the artifact that was actually certified.
# HONUA_DUCKDB_SERVER_VERSION / _COMMIT set by the caller always win.
if [[ -z "${HONUA_DUCKDB_SERVER_VERSION:-}" ]]; then
    resolved_version="$(
        curl -fsS -H "X-API-Key: ${HONUA_ADMIN_API_KEY}" \
            "${HONUA_BASE_URL}/api/v1/admin/version" 2>/dev/null |
        python3 -c 'import json,sys; print(json.load(sys.stdin).get("data",{}).get("version",""))' \
            2>/dev/null || true
    )"
    if [[ -n "${resolved_version}" ]]; then
        export HONUA_DUCKDB_SERVER_VERSION="${resolved_version}"
    fi
fi

# --confcutdir stops pytest from loading tests/python/conftest.py, which imports
# testcontainers/psycopg/geopandas to manage a local PostGIS fixture. This lane
# certifies an already-running deployment, so that machinery is neither wanted
# nor installed in this image.
# -p no:cacheprovider: tests/ is bind-mounted read-only, and the cache plugin
# otherwise emits a warning per run trying to write .pytest_cache into it.
pytest tests/python/duckdb_client \
    --confcutdir=tests/python/duckdb_client \
    --override-ini="addopts=" \
    -p no:cacheprovider \
    -v \
    --tb=short
status=$?

# The envelope is written from a session-teardown fixture, so it exists even
# when cases fail. Guarantee it is present before propagating a non-zero exit;
# a lane that fails without evidence is indistinguishable from a lane that
# never ran, and the strict baseline diff must be able to tell them apart.
if ! ls /output/*-duckdb-ogc-features.cert.json >/dev/null 2>&1; then
    echo "ERROR: the DuckDB lane produced no .cert.json envelope in /output." >&2
    if [[ "${status}" -eq 0 ]]; then
        status=1
    fi
fi

echo "DuckDB Spatial lane complete (pytest exit ${status}); output written to /output."
exit "${status}"
