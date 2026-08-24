#!/usr/bin/env bash
# Runs the GeoPandas/pyogrio client certification lane against the Compose
# honua service and writes the per-protocol .cert.json envelopes to /output
# for the baseline-diff step.
#
# docker/client-compat/compose.yml must mount:
#     ../../tests:/workspace/tests:ro
#     ./output/geopandas:/output
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_GEOPANDAS_SERVICE_ID:=test_service}"
: "${HONUA_GEOPANDAS_COLLECTION_ID:=0}"

cd /workspace

# The compose healthcheck already orders this lane behind a healthy honua, but
# a few belt-and-braces retries cover slow first boots.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/python/geopandas_client/conftest.py honors HONUA_GEOPANDAS_OUTPUT_DIR
# and writes the .cert.json envelopes there directly, so the lane never needs
# to write into the read-only tests/ bind mount.
export HONUA_GEOPANDAS_BASE_URL="${HONUA_BASE_URL}"
export HONUA_GEOPANDAS_SERVICE_ID
export HONUA_GEOPANDAS_COLLECTION_ID
export HONUA_GEOPANDAS_OUTPUT_DIR=/output

# cert_envelope.read_server_version() probes /api/v1/admin/version anonymously,
# but that route is behind the admin API key, so the envelope would record
# server_version="unknown". Resolve it here with the bootstrap key and pass it
# through the documented override env var.
if [[ -z "${HONUA_GEOPANDAS_SERVER_VERSION:-}" ]]; then
    resolved_version=$(
        curl -fsS \
            -H "X-API-Key: ${HONUA_ADMIN_API_KEY:-ClientCompatAdmin123!}" \
            "${HONUA_BASE_URL}/api/v1/admin/version" 2>/dev/null \
        | python -c 'import json,sys; d=json.load(sys.stdin); print(d.get("data",{}).get("version") or d.get("version") or "")' 2>/dev/null \
        || true
    )
    if [[ -n "${resolved_version}" ]]; then
        export HONUA_GEOPANDAS_SERVER_VERSION="${resolved_version}"
    fi
fi

# CERT failures are evidence, not infrastructure faults: the envelope is the
# gate, so a red case must still produce output and a zero exit. `|| true` is
# scoped to the test runner only, and the envelope-presence check below is what
# actually distinguishes "lane ran and reported" from "lane broke".
python -m pytest tests/python/geopandas_client \
    --override-ini="addopts=" \
    -v \
    --tb=short \
    || true

shopt -s nullglob
envelopes=(/output/*-py-geopandas-*.cert.json)
if [[ ${#envelopes[@]} -eq 0 ]]; then
    echo "GeoPandas lane FAILED: no .cert.json envelope was written to /output." >&2
    exit 1
fi

echo "GeoPandas lane complete; ${#envelopes[@]} envelope(s) written to /output."
