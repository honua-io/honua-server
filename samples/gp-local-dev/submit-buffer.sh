#!/usr/bin/env bash
# Submit a geoprocessing job to a LOCAL Honua dev server, poll it, and fetch
# results — using only the in-process local backend (no cloud, no Batch).
#
# Prereq: the GP dev stack is up:
#   docker compose -f docker-compose.gp-dev.yml up
#
# What it does: buffers a point (POINT(-122.4194 37.7749)) by 500 m via the
# canonical OGC API Processes `geometry.buffer` process. The job runs IN-PROCESS
# inside the honua container on the LocalBatchComputeBackend ("local"); Redis
# carries the job queue / status / result package.
#
# Usage:
#   ./submit-buffer.sh
#   BASE=http://localhost:8080 KEY=quickstart-admin-password ./submit-buffer.sh
set -euo pipefail

BASE="${BASE:-http://localhost:8080}"
# The admin password (compose default) doubles as the X-API-Key. The admin role
# holds the process-execute grant the GP submit path requires.
KEY="${KEY:-quickstart-admin-password}"
PROCESS="${PROCESS:-geometry.buffer}"

# POINT(-122.4194 37.7749) as base64-encoded little-endian WKB.
WKB="${WKB:-AQEAAABQ/Bhz15pewNDVVuwv40JA}"
DISTANCE="${DISTANCE:-500}"

echo "==> Server health"
curl -fsS "$BASE/healthz/ready" && echo " ok" || { echo "server not ready at $BASE"; exit 1; }

echo "==> Discover the process catalog (anonymous)"
curl -fsS "$BASE/ogc/processes/processes" >/dev/null && echo "    catalog reachable"

echo "==> Submit $PROCESS (async)"
SUBMIT_HEADERS="$(mktemp)"
SUBMIT_BODY="$(curl -fsS -D "$SUBMIT_HEADERS" \
  -X POST "$BASE/ogc/processes/processes/$PROCESS/execution" \
  -H "X-API-Key: $KEY" \
  -H "Content-Type: application/json" \
  -H "Prefer: respond-async" \
  -d "{\"inputs\":{\"wkb\":\"$WKB\",\"srid\":4326,\"distance\":$DISTANCE}}")"

# The job id is in the Location header (.../jobs/{jobId}) and the jobID field.
JOB="$(grep -i '^location:' "$SUBMIT_HEADERS" | tr -d '\r' | sed -E 's@.*/jobs/@@')"
rm -f "$SUBMIT_HEADERS"
if [ -z "${JOB:-}" ]; then
  JOB="$(printf '%s' "$SUBMIT_BODY" | sed -E 's/.*"jobID"[: ]*"([^"]+)".*/\1/')"
fi
echo "    jobID=$JOB"

echo "==> Poll job status until terminal"
for _ in $(seq 1 60); do
  STATUS_DOC="$(curl -fsS "$BASE/ogc/processes/jobs/$JOB" -H "X-API-Key: $KEY")"
  STATUS="$(printf '%s' "$STATUS_DOC" | sed -E 's/.*"status"[: ]*"([^"]+)".*/\1/')"
  echo "    status=$STATUS"
  case "$STATUS" in
    successful) break ;;
    failed|dismissed) echo "    job did not succeed:"; echo "$STATUS_DOC"; exit 1 ;;
  esac
  sleep 1
done

echo "==> Fetch results"
curl -fsS "$BASE/ogc/processes/jobs/$JOB/results" -H "X-API-Key: $KEY"
echo

echo "==> Dismiss the job (cleanup)"
curl -fsS -X DELETE "$BASE/ogc/processes/jobs/$JOB" -H "X-API-Key: $KEY" >/dev/null && echo "    dismissed"
