#!/usr/bin/env bash
# Runs the PyQGIS client compatibility suite against a running honua service
# and copies the generated .cert.json envelopes into /output.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_PYQGIS_SERVICE_ID:=test_service}"
: "${HONUA_PYQGIS_COLLECTION_ID:=0}"

cd /workspace

for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

export HONUA_PYQGIS_BASE_URL="${HONUA_BASE_URL}"
export HONUA_PYQGIS_SERVICE_ID
export HONUA_PYQGIS_COLLECTION_ID
export HONUA_PYQGIS_REQUIRE_WFS=1
export QT_QPA_PLATFORM=offscreen

xvfb-run -a pytest tests/python/pyqgis \
    -m pyqgis \
    --tb=short \
    -v \
    || true

shopt -s nullglob
for f in tests/TestResults/*-desktop-qgis-*.cert.json; do
    cp "$f" /output/
done

echo "PyQGIS lane complete; output written to /output."
