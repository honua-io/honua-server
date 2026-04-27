#!/usr/bin/env bash
# Runs the OpenLayers (and js-browser MapLibre + Esri Leaflet) interop suites
# against a running honua service and copies emitted .cert.json envelopes
# into /output.
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

# OpenLayers Vitest suite.
if [[ -f tests/js/package.json ]]; then
    pushd tests/js >/dev/null
    npm install --no-audit --no-fund --prefer-offline
    HONUA_BASE_URL="${HONUA_BASE_URL}" \
        npx vitest run --reporter=default || true
    popd >/dev/null

    shopt -s nullglob
    for f in tests/js/*.cert.json tests/js/certification-evidence/*.cert.json; do
        cp "$f" /output/ 2>/dev/null || true
    done
fi

# Esri Leaflet + MapLibre Playwright suites (browser-side).
if [[ -f tests/js-browser/package.json ]]; then
    pushd tests/js-browser >/dev/null
    npm install --no-audit --no-fund --prefer-offline
    HONUA_BASE_URL="${HONUA_BASE_URL}" \
        npx playwright test --config playwright.maplibre.config.ts || true
    popd >/dev/null

    shopt -s nullglob
    for f in tests/js-browser/test-results/*.cert.json; do
        cp "$f" /output/ 2>/dev/null || true
    done
fi

echo "OpenLayers lane complete; output written to /output."
