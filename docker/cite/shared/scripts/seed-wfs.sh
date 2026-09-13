#!/usr/bin/env bash
set -euo pipefail

# Read the journal only. Do not repair schema or manufacture migration receipts.
# Check the metadata floor first so old fixtures fail with the same actionable
# migration name as the production schema guard, before any seed writes occur.
journal="$(mktemp)"
trap 'rm -f "$journal"' EXIT
if [[ "$(psql -XAt -v ON_ERROR_STOP=1 -c "SELECT to_regclass('public.schema_versions') IS NOT NULL")" == t ]]; then
  psql -XAt -v ON_ERROR_STOP=1 -c 'SELECT scriptname FROM public.schema_versions' > "$journal"
fi
require_migration() {
  if ! grep -Fxq "Honua.Server.Migrations.$1" "$journal"; then
    echo "CITE schema guard: missing migration Honua.Server.Migrations.$1; boot the pinned server with migrations enabled before seeding." >&2
    exit 1
  fi
}
require_migration 031_CreateMetadataV2Snapshot.sql
for migration in /migrations/*.sql; do
  name="${migration##*/}"
  # The production runner omits configured-schema adoption for default honua.
  [[ "$name" == 109_AdoptConfiguredGuardedSchema.sql ]] && continue
  require_migration "$name"
done
for seed in /cite-data/*.sql; do
  psql -X -v ON_ERROR_STOP=1 -f "$seed"
done
