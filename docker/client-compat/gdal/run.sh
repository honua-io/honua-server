#!/usr/bin/env bash
# Runs the GDAL/OGR interop suite against the Compose honua service and writes
# the JSON evidence envelope plus per-protocol .cert.json envelopes to /output
# for the baseline-diff step.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_GDAL_SERVICE_ID:=test_service}"
: "${HONUA_GDAL_COLLECTION_ID:=0}"

cd /workspace

# Wait for honua to come up — the compose healthcheck handles ordering, but a
# few belt-and-braces retries cover slow first-boots.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/python/gdal_ogr/conftest.py honors HONUA_BASE_URL (skipping the local
# ``honua_server``/``postgis`` chain) and writes its evidence report to
# HONUA_GDAL_RESULTS_PATH so we never need to write into the read-only
# tests/ bind mount.
export HONUA_BASE_URL HONUA_GDAL_SERVICE_ID HONUA_GDAL_COLLECTION_ID
export HONUA_GDAL_RESULTS_PATH=/output/gdal-ogr-results.json
# CERT failures are evidence, not infrastructure faults: the envelope is the
# gate, so a red case must still produce output and a zero exit here. `|| true`
# is scoped to the test runner only - the envelope-presence checks below are
# what actually distinguish "lane ran and reported" from "lane broke"
# (honua-server#4420; this lane previously had neither check, unlike the
# geopandas and duckdb lanes).
pytest tests/python/gdal_ogr \
    --tb=short \
    -v \
    --override-ini="addopts=" \
    || true

if [[ ! -f /output/gdal-ogr-results.json ]]; then
    echo "GDAL/OGR lane FAILED: no gdal-ogr-results.json was written to /output." >&2
    exit 1
fi

# Convert the GDAL custom JSON into per-protocol .cert.json envelopes so the
# baseline-diff step in client-interop-nightly can compare GDAL results
# against tests/baselines/client-compat/gdal/. The converter is mounted at
# /workspace/scripts/client-compat by docker/client-compat/compose.yml.
python3 /workspace/scripts/client-compat/convert-gdal-results.py \
    --input /output/gdal-ogr-results.json \
    --output-dir /output

shopt -s nullglob
envelopes=(/output/*-cli-gdal-*.cert.json)
if [[ ${#envelopes[@]} -eq 0 ]]; then
    echo "GDAL/OGR lane FAILED: no .cert.json envelope was written to /output." >&2
    exit 1
fi

echo "GDAL/OGR lane complete; ${#envelopes[@]} envelope(s) written to /output."
