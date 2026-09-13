#!/usr/bin/env bash
# Raster extension discovery must precede migrations; seed/restart must retain the floor.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cd "$repo_root"
: "${HONUA_CITE_SERVER_IMAGE:?Set the pinned server image to test}"
export HONUA_CITE_SERVER_IMAGE HONUA_CITE_WCS20_SERVER_PORT=0 HONUA_CITE_WCS20_POSTGRES_PORT=0
compose=(docker compose -p "cite-wcs-bootstrap-${BASHPID}" -f docker/cite/wcs20/compose.yml)
finish() {
  result=$?
  if (( result != 0 )); then
    "${compose[@]}" logs honua-server postgres >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans >/dev/null
  exit "$result"
}
trap finish EXIT
"${compose[@]}" up --no-build --wait --wait-timeout 120 honua-server
# All provider migrations must have run from the image before the data seed.
count="$("${compose[@]}" exec -T postgres psql -U postgres -d honua_cite_wcs -Atc "SELECT count(*) FROM public.schema_versions WHERE scriptname LIKE 'Honua.Postgres.Migrations.%'")"
[[ "$count" -ge 5 ]]
"${compose[@]}" stop honua-server
"${compose[@]}" exec -T postgres psql -U postgres -d honua_cite_wcs -v ON_ERROR_STOP=1 < docker/cite/wcs20/seed.sql
"${compose[@]}" up --no-build --wait --wait-timeout 120 honua-server
"${compose[@]}" exec -T honua-server wget -T 15 -qO- http://localhost:8080/healthz/ready
count="$("${compose[@]}" exec -T postgres psql -U postgres -d honua_cite_wcs -Atc 'SELECT count(*) FROM honua.raster_data')"
[[ "$count" == 2 ]]
# Check the actual migration effect that the old handcrafted seed omitted.
storage="$("${compose[@]}" exec -T postgres psql -U postgres -d honua_cite_wcs -Atc "SELECT attstorage FROM pg_attribute WHERE attrelid = 'honua.raster_data'::regclass AND attname = 'raster'")"
[[ "$storage" == e ]]
capabilities="$("${compose[@]}" exec -T honua-server wget -T 15 -qO- 'http://localhost:8080/ogc/services/cite/wcs?service=WCS&version=2.0.1&request=GetCapabilities')"
grep -q 'CoverageSummary' <<< "$capabilities"
echo 'PASS: WCS extension-first migration, data-only seed, restart and raster storage floor'
