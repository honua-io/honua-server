#!/usr/bin/env bash
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
#
# Applies the demo.honua.io STAC imagery seed (honua-server#1688) to a target
# database. Wraps tests/seed/demo-stac-imagery-v1.sql, which seeds Sentinel-2-style
# scene features and merges two STAC collections into the active Metadata v2
# snapshot so GET /stac/collections is non-empty and POST /stac/search returns
# items for a Maui-area bbox.
#
# This script applies ONLY the STAC seed. Pro-license + geocoding/streaming/editing
# enablement is operator-run and documented in
# docs/internal/demo/demo-honua-io-capability-runbook.md.
#
# Usage:
#   apply-demo-stac-seed.sh
#
# Environment variables:
#   PGPASSWORD                psql password (required)
#   PGHOST                    db host (default localhost)
#   PGPORT                    db port (default 5432)
#   PGUSER                    db user (default honua)
#   PGDATABASE                db name (default honua)
#   HONUA_SEED_ENV            Metadata v2 environment id. MUST match the server's
#                             Metadata__Environment / Environment setting (default "default").
#   HONUA_SEED_SCHEMA         Honua metadata schema (default "honua").
#
# On demo.honua.io the database is reachable only in-VPC. The supported channel is
# the bootstrap Lambda (honua-demo-demo-postgis-bootstrap), which accepts
# {"statements":[...]} / {"query":"..."}. To run there, send the contents of
# demo-stac-imagery-v1.sql as a single statement (the file is one transaction).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SEED_FILE="${SCRIPT_DIR}/demo-stac-imagery-v1.sql"

if [ ! -f "${SEED_FILE}" ]; then
  echo "Error: seed file not found at ${SEED_FILE}" >&2
  exit 1
fi

SEED_ENV="${HONUA_SEED_ENV:-default}"
SEED_SCHEMA="${HONUA_SEED_SCHEMA:-honua}"

echo "Applying demo STAC seed: env=${SEED_ENV} schema=${SEED_SCHEMA} -> ${PGHOST:-localhost}:${PGPORT:-5432}/${PGDATABASE:-honua}"

PGPASSWORD="${PGPASSWORD:?PGPASSWORD is required}" psql \
  -v ON_ERROR_STOP=1 \
  -h "${PGHOST:-localhost}" \
  -p "${PGPORT:-5432}" \
  -U "${PGUSER:-honua}" \
  -d "${PGDATABASE:-honua}" \
  -v env="${SEED_ENV}" \
  -v schema="${SEED_SCHEMA}" \
  -f "${SEED_FILE}"

echo
echo "Done. Verify with:"
echo "  curl -s https://demo.honua.io/stac/collections | jq '.collections | length'   # expect >= 2"
echo "  curl -s -X POST https://demo.honua.io/stac/search \\"
echo "    -H 'content-type: application/json' \\"
echo "    -d '{\"bbox\":[-156.70,20.60,-156.30,20.96],\"collections\":[\"90810\"]}' | jq '.features | length'"
