#!/usr/bin/env bash
# Runs the GDAL/OGR interop suite against the running honua service and writes
# the JSON evidence envelope to /output for the baseline-diff step.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"

cd /workspace

# Wait for honua to come up — the compose healthcheck handles ordering, but a
# few belt-and-braces retries cover slow first-boots.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

export HONUA_BASE_URL
mkdir -p /output

# tests/python/conftest.py expects to manage its own server fixture. The lane
# overrides via env var; the gdal_ogr suite reads HONUA_BASE_URL when present.
HONUA_PYQGIS_BASE_URL="${HONUA_BASE_URL}" \
HONUA_BASE_URL="${HONUA_BASE_URL}" \
pytest tests/python/gdal_ogr \
    --tb=short \
    -v \
    --override-ini="addopts=" \
    || true

# Copy evidence to /output. tests/python/gdal-ogr-results.json is written by
# the conftest at session teardown.
if [[ -f tests/python/gdal-ogr-results.json ]]; then
    cp tests/python/gdal-ogr-results.json /output/
fi

# If a .cert.json envelope was produced by the suite (some sub-modules emit
# them), copy those too.
shopt -s nullglob
for f in tests/python/*.cert.json tests/TestResults/*.cert.json; do
    cp "$f" /output/ 2>/dev/null || true
done

echo "GDAL/OGR lane complete; output written to /output."
