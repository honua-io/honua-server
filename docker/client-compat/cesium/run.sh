#!/usr/bin/env bash
# Runs the Cesium browser interop suite against a running honua service and
# copies emitted .cert.json envelopes into /output.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"

cd /workspace

for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

pushd tests/js-browser/cesium >/dev/null
npm install --no-audit --no-fund --prefer-offline
HONUA_BASE_URL="${HONUA_BASE_URL}" \
    npx playwright test --config playwright.config.ts || true
popd >/dev/null

shopt -s nullglob
for f in tests/js-browser/test-results/*-js-cesium-*.cert.json; do
    cp "$f" /output/
done

echo "Cesium lane complete; output written to /output."
