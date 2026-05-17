#!/usr/bin/env bash
# Reset the local test schema and seed the admin preview sample FeatureServer.
#
# Environment variables:
#   PGPASSWORD, PGHOST (default localhost), PGPORT (default 5432),
#   PGUSER (default honua), PGDATABASE (default honua_test)
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

export PGPASSWORD="${PGPASSWORD:-honua}"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"
export PGUSER="${PGUSER:-honua}"
export PGDATABASE="${PGDATABASE:-honua_test}"

psql \
  -v ON_ERROR_STOP=1 \
  -h "$PGHOST" \
  -p "$PGPORT" \
  -U "$PGUSER" \
  -d "$PGDATABASE" \
  -f "$ROOT_DIR/tests/seed/base-schema.sql"

bash "$ROOT_DIR/tests/seed/apply-yaml-seed.sh" \
  "$ROOT_DIR/tests/seed/admin-sample-feature-server.yaml"
