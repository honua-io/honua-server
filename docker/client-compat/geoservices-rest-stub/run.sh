#!/usr/bin/env bash
# Runs the geoservices-rest-stub REST lane against a running honua service.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${GEOSERVICES_STUB_SERVICE_NAME:=browser_compat}"
: "${GEOSERVICES_STUB_LAYER_ID:=2000}"

for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

HONUA_BASE_URL="${HONUA_BASE_URL}" \
GEOSERVICES_STUB_SERVICE_NAME="${GEOSERVICES_STUB_SERVICE_NAME}" \
GEOSERVICES_STUB_LAYER_ID="${GEOSERVICES_STUB_LAYER_ID}" \
GEOSERVICES_STUB_OUTPUT_DIR=/output \
    python3 /usr/local/bin/stub_runner.py

echo "GeoServices REST stub lane complete; output written to /output."
