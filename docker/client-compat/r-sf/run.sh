#!/usr/bin/env bash
# Runs the R sf/ows4R certification suite against the Compose honua service and
# writes one .cert.json envelope per protocol (ogc-features, wfs) into /output
# for the baseline-diff step.
#
# Mounts docker/client-compat/compose.yml must provide for this lane:
#   ../../tests:/workspace/tests:ro     the R suite plus tests/seed and
#                                       tests/config, whose digests become the
#                                       envelope's fixture_revision /
#                                       server_config_revision receipts
#   ./output/r-sf:/output               envelope output directory
set -uo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_R_SF_SERVICE_ID:=test_service}"
: "${HONUA_R_SF_COLLECTION_ID:=0}"

cd /workspace

# The compose healthcheck already orders this lane after honua is healthy; a
# few belt-and-braces retries cover slow first-boots.
for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/ is bind-mounted read-only, so the suite must never write beside itself:
# HONUA_R_SF_OUTPUT_DIR points the envelope writer at /output instead.
export HONUA_R_SF_BASE_URL="${HONUA_BASE_URL}"
export HONUA_R_SF_OUTPUT_DIR=/output
export HONUA_R_SF_SERVICE_ID
export HONUA_R_SF_COLLECTION_ID
export HONUA_R_CERT_DIR=/workspace/tests/r/certification

# The driver traps every case individually and always writes both envelopes,
# including when cases fail; `|| true` keeps a non-zero exit (a hard
# configuration error) from discarding whatever evidence did reach /output,
# matching the gdal/pyqgis lanes.
Rscript /workspace/tests/r/certification/run_sf_lane.R || true

echo "R sf/ows4R lane complete; output written to /output."
ls -1 /output || true
