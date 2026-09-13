#!/usr/bin/env bash
# Live regression: current migrations precede seed writes; stale journals fail by name.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cd "$repo_root"
: "${HONUA_CITE_SERVER_IMAGE:?Set the pinned server image to test}"
export HONUA_CITE_SERVER_IMAGE
project="cite-bootstrap-${BASHPID}"
compose_file=""
cleanup() {
  if [[ -n "$compose_file" ]]; then
    docker compose -p "$project" -f "$compose_file" down --volumes --remove-orphans >/dev/null
  fi
}
finish() {
  result=$?
  if (( result != 0 )) && [[ -n "$compose_file" ]]; then
    docker compose -p "$project" -f "$compose_file" logs honua-server seed postgres >&2 || true
  fi
  cleanup
  exit "$result"
}
trap finish EXIT
# Random host ports keep this test isolated from other local/CI fixtures.
export HONUA_CITE_WFS10_SERVER_PORT=0 HONUA_CITE_WFS10_POSTGRES_PORT=0
export HONUA_CITE_WFS11_SERVER_PORT=0 HONUA_CITE_WFS11_POSTGRES_PORT=0
export HONUA_CITE_WFS20_SERVER_PORT=0 HONUA_CITE_WFS20_POSTGRES_PORT=0
for suite in wfs10 wfs11 wfs20; do
  compose_file="docker/cite/$suite/compose.yml"
  compose=(docker compose -p "$project" -f "$compose_file")
  "${compose[@]}" up -d seed
  seed_id="$("${compose[@]}" ps -aq seed)"
  exit_code="$(docker wait "$seed_id")"
  "${compose[@]}" logs seed
  [[ "$exit_code" == 0 ]]
  "${compose[@]}" exec -T honua-server wget -T 15 -qO- http://localhost:8080/healthz/ready
  expected=14
  [[ "$suite" == wfs20 ]] && expected=2
  actual="$("${compose[@]}" exec -T postgres psql -U postgres -d honua_cite -Atc 'SELECT count(*) FROM honua.layers')"
  [[ "$actual" == "$expected" ]]
  "${compose[@]}" exec -T honua-server wget -T 15 -qO- 'http://localhost:8080/wfs?service=WFS&version=2.0.0&request=GetCapabilities' > /tmp/"$project"-capabilities.xml
  grep -q 'admin_boundaries' /tmp/"$project"-capabilities.xml
  rm /tmp/"$project"-capabilities.xml
  # A journal mismatch must be rejected before any data mutation, with the
  # exact missing migration in the diagnostic. Only mutate our throwaway DB.
  "${compose[@]}" exec -T postgres psql -U postgres -d honua_cite -v ON_ERROR_STOP=1 -c "DELETE FROM public.schema_versions WHERE scriptname = 'Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql'"
  if output="$("${compose[@]}" run --rm --no-deps seed 2>&1)"; then
    echo 'ERROR: missing schema floor was accepted' >&2
    exit 1
  fi
  grep -F 'missing migration Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql' <<< "$output"
  actual="$("${compose[@]}" exec -T postgres psql -U postgres -d honua_cite -Atc 'SELECT count(*) FROM honua.layers')"
  [[ "$actual" == "$expected" ]]
  cleanup
  compose_file=""
  echo "PASS: $suite migration-first boot, seeded catalog, and missing-floor rejection"
done
