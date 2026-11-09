#!/usr/bin/env bash
set -euo pipefail

# Read the journal only. Do not repair schema or manufacture migration receipts.
# Check the metadata floor first so old fixtures fail with the same actionable
# migration name as the production schema guard, before any seed writes occur.
journal="$(mktemp)"
graph_sql="$(mktemp)"
trap 'rm -f "$journal" "$graph_sql"' EXIT
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

{
  echo 'CREATE TEMP TABLE cite_source_graph AS'
  awk '
    /internal const string BuildDocumentFromV1Catalog =/ { declaration = 1; next }
    declaration && /^[[:space:]]*"""/ {
      if (reading) { closed = 1; exit }
      reading = 1
      next
    }
    reading {
      gsub(/__CATALOG_SCHEMA__/, "honua")
      gsub(/@environment/, "\047Test\047")
      print
    }
    END { if (!closed) exit 1 }
  ' /cite-scripts/compat-graph.cs
  echo ';'
  cat /cite-scripts/author-wfs-publications.sql
} > "$graph_sql"
psql -X -v ON_ERROR_STOP=1 -f "$graph_sql"
