#!/usr/bin/env bash
# Runs the pystac-client certification lane against the Compose honua service and
# writes the py-pystac/stac .cert.json envelope to /output for the
# baseline-diff step.
#
# Compose mounts (docker/client-compat/compose.yml):
#   ../../tests:/workspace/tests:ro   - the suite, read-only
#   ./output/pystac:/output           - envelope destination
#
# Deliberately NOT `set -e` around pytest: a failing certification case must
# still leave an envelope behind, because a missing envelope reads as "the lane
# did not run" while a fail-status envelope is the actual evidence.
set -uo pipefail

# Compose sets HONUA_BASE_URL plus the suite's own HONUA_STAC_COMPAT_* prefix;
# honor whatever it provided and only fall back to the canonical fixture values.
: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_PYSTAC_SERVICE_ID:=${HONUA_STAC_COMPAT_SERVICE_ID:-test_service}}"
: "${HONUA_PYSTAC_COLLECTION_ID:=${HONUA_STAC_COMPAT_COLLECTION_ID:-0}}"

cd /workspace

# Belt-and-braces readiness wait. The compose healthcheck already orders this
# container after honua is healthy; these retries cover a slow first boot.
for _ in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/python/stac_client/conftest.py honors HONUA_STAC_COMPAT_BASE_URL (and
# HONUA_BASE_URL as the shared fallback), which short-circuits the local
# PostGIS/server startup chain, and writes both the .cert.json envelope and the
# bespoke stac-client-compat report into HONUA_PYSTAC_OUTPUT_DIR so the lane
# never writes into the read-only tests/ bind mount.
export HONUA_BASE_URL
export HONUA_STAC_COMPAT_BASE_URL="${HONUA_STAC_COMPAT_BASE_URL:-${HONUA_BASE_URL}}"
export HONUA_STAC_COMPAT_SERVICE_ID="${HONUA_PYSTAC_SERVICE_ID}"
export HONUA_STAC_COMPAT_COLLECTION_ID="${HONUA_PYSTAC_COLLECTION_ID}"
export HONUA_PYSTAC_OUTPUT_DIR=/output

# Receipt bindings. tests/python/shared/cert_envelope.py reads the server
# version from /api/v1/admin/version anonymously, which the control plane
# (correctly) answers with 401, so the envelope would otherwise record
# server_version="unknown". Resolve it here with the fixture admin key and hand
# it to the collector through the documented override env var. The commit has no
# in-container source at all (only tests/ is mounted, not .git), so compose
# should pass HONUA_PYSTAC_SERVER_COMMIT when it knows the built revision.
: "${HONUA_PYSTAC_ADMIN_API_KEY:=${HONUA_ADMIN_API_KEY:-ClientCompatAdmin123!}}"
if [[ -z "${HONUA_PYSTAC_SERVER_VERSION:-}" ]]; then
    resolved_version=$(
        curl -fsS -H "X-API-Key: ${HONUA_PYSTAC_ADMIN_API_KEY}" \
            "${HONUA_BASE_URL}/api/v1/admin/version" 2>/dev/null |
        python3 -c 'import json,sys
try:
    payload = json.load(sys.stdin)
except Exception:
    raise SystemExit(0)
data = payload.get("data") if isinstance(payload, dict) else None
version = (data or {}).get("version") if isinstance(data, dict) else None
print(version or (payload.get("version") if isinstance(payload, dict) else "") or "")' 2>/dev/null
    )
    if [[ -n "${resolved_version}" ]]; then
        export HONUA_PYSTAC_SERVER_VERSION="${resolved_version}"
    fi
fi
export HONUA_PYSTAC_SERVER_COMMIT="${HONUA_PYSTAC_SERVER_COMMIT:-}"

# The whole tests/python/stac_client package runs, with one exclusion:
# test_stac_api_validator.py is the pre-existing bespoke lane's hard-failing
# wrapper around the external validator. This lane runs the same validator from
# test_cert_common_core.py::test_nb_valid_01_stac_api_validator instead, where
# its findings are recorded as the NB-STAC-VALID-01 extension result and an
# unreachable remote schema degrades to a recorded skip rather than crashing the
# lane.
#
# --override-ini="addopts=" drops the repo-wide `-v --tb=short` default so the
# lane controls its own reporting.
pytest tests/python/stac_client \
    --ignore=tests/python/stac_client/test_stac_api_validator.py \
    --override-ini="addopts=" \
    --tb=short \
    -v
pytest_status=$?

# Guarantee the envelope exists before exiting: the session-teardown fixture in
# conftest.py writes it, but a collection-time error would abort before any
# fixture ran, and the baseline-diff step must be able to tell "lane ran and
# failed" from "lane never produced evidence".
if ! ls /output/*"-py-pystac-stac.cert.json" >/dev/null 2>&1; then
    echo "::warning::pystac lane produced no .cert.json envelope (pytest exit ${pytest_status}); emitting a fail-closed placeholder."
    python3 - <<'PY'
import os
import sys

sys.path.insert(0, "/workspace/tests/python")

from shared import cert_envelope  # noqa: E402
from stac_client import cert_lane  # noqa: E402

runtime = cert_envelope.LaneRuntime(
    base_url=os.environ.get("HONUA_BASE_URL", ""),
    environment="ci" if os.getenv("CI") else "local",
    server_version="unknown",
    server_commit="unknown",
    fixture_revision="unknown",
    server_config_revision="unknown",
)
collector = cert_envelope.CertificationEvidenceCollector(
    runtime,
    client_lane=cert_lane.CLIENT_LANE,
    client_version=cert_lane.client_version(),
    protocol=cert_lane.PROTOCOL,
    protocol_version="unknown",
    applicable=cert_lane.APPLICABLE_CASES,
    not_applicable_reason=cert_lane.NOT_APPLICABLE_REASON,
)
path = cert_lane.envelope_path()
collector.write_envelope(path)
print(f"wrote fail-closed placeholder envelope to {path}")
PY
fi

echo "pystac lane complete (pytest exit ${pytest_status}); output written to /output."
ls -l /output
exit 0
