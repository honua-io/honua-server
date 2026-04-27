#!/usr/bin/env bash
# Runs the ArcGIS Pro REST stub against a running honua service.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${ARCGIS_STUB_SERVICE_NAME:=browser_compat}"
: "${ARCGIS_STUB_LAYER_ID:=2000}"

for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

HONUA_BASE_URL="${HONUA_BASE_URL}" \
ARCGIS_STUB_SERVICE_NAME="${ARCGIS_STUB_SERVICE_NAME}" \
ARCGIS_STUB_LAYER_ID="${ARCGIS_STUB_LAYER_ID}" \
ARCGIS_STUB_OUTPUT_DIR=/output \
    python3 /usr/local/bin/stub_runner.py

echo "ArcGIS stub lane complete; output written to /output."
