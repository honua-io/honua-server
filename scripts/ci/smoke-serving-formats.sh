#!/usr/bin/env bash
# Native readers and font rendering must be exercised in the built runtime, where SDK libraries are absent.
# Usage: smoke-serving-formats.sh <image> [output-dir] [server-port] [postgres-port]
set -euo pipefail

IMAGE="${1:?image reference required (tag or image@sha256:...)}"
OUT="${2:-$(mktemp -d)}"
PORT="${3:-18912}"
PG_PORT="${4:-18913}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/cng/compose.yml"
SEED_FILE="$REPO_ROOT/docker/cng/seed.sql"
PROJECT="honua-serving-formats-smoke-$$"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

work="$(mktemp -d)"
: > "$work/empty-secret"
cat > "$work/override.yml" <<EOF
services:
  honua-server:
    image: $IMAGE
    restart: "no"
EOF
export HONUA_CNG_SERVER_PORT="$PORT" HONUA_CNG_POSTGRES_PORT="$PG_PORT"
export HONUA_GITHUB_ACTOR_SECRET_FILE="$work/empty-secret" HONUA_GITHUB_TOKEN_SECRET_FILE="$work/empty-secret"
compose() { docker compose -p "$PROJECT" -f "$COMPOSE_FILE" -f "$work/override.yml" "$@"; }
cleanup() {
  compose logs --no-color honua-server > "$OUT/honua-server.log" 2>&1 || true
  compose down --remove-orphans --volumes >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

python3 -m venv "$work/python"
format_python="$work/python/bin/python"
"$format_python" -m pip install --quiet --only-binary=:all: --require-hashes -r "$REPO_ROOT/scripts/ci/requirements-image-smoke.txt"
"$format_python" "$REPO_ROOT/scripts/ci/test-serving-formats.py"
format_assertions="$REPO_ROOT/scripts/ci/verify-serving-formats.py"

fail() {
  echo "::error::Serving format smoke failed for ${IMAGE}: $*" >&2
  compose logs --no-color --tail 80 honua-server >&2 || true
  exit 1
}

wait_healthy() {
  local svc="$1" deadline=$((SECONDS + $2)) id
  id="$(compose ps -q "$svc")"
  until [[ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" == healthy ]]; do
    if (( SECONDS > deadline )); then fail "$svc did not become healthy"; fi
    sleep 5
  done
}

verify_persisted_store_refusal() {
  local profile id deadline exit_code
  profile="$(docker image inspect "$IMAGE" --format '{{ index .Config.Labels "honua.store.profile" }}')"
  [[ "$profile" == postgis ]] || return 0
  compose stop honua-server
  docker exec -i "$pg" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cng <<'SQL'
INSERT INTO honua.data_connections
  (name, host, port, database_name, username, provider_name, secret_ref, secret_type)
VALUES
  ('image-excluded-store', 'localhost', 3306, 'fixture', 'fixture', 'mysql',
   'env:HONUA_FORMAT_SMOKE', 'environment');
SQL
  compose up -d --no-build honua-server
  id="$(compose ps -a -q honua-server)"
  deadline=$((SECONDS + 90))
  while [[ "$(docker inspect -f '{{.State.Running}}' "$id")" == true ]]; do
    if (( SECONDS > deadline )); then fail 'persisted excluded store did not refuse startup'; fi
    sleep 1
  done
  exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$id")"
  [[ "$exit_code" != 0 ]] || fail 'persisted excluded store exited successfully'
  compose logs --no-color honua-server > "$OUT/persisted-store-refusal.log"
  grep -F 'This image includes only the PostGIS data store.' "$OUT/persisted-store-refusal.log" >/dev/null \
    || fail 'persisted refusal did not name the store policy'
  grep -F -- '-full' "$OUT/persisted-store-refusal.log" >/dev/null \
    || fail 'persisted refusal did not name the full variant'
  printf 'profile=%s\nexit_code=%s\n' "$profile" "$exit_code" > "$OUT/persisted-store-refusal.txt"
}

docker image inspect "$IMAGE" \
  --format 'image={{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}{{"\n"}}revision={{index .Config.Labels "org.opencontainers.image.revision"}}{{"\n"}}architecture={{.Architecture}}' \
  > "$OUT/image.txt"

compose up -d --no-build postgres redis
wait_healthy postgres 180
wait_healthy redis 60
compose up -d --no-build honua-server
wait_healthy honua-server 300
compose stop honua-server
pg="$(compose ps -q postgres)"
docker cp "$SEED_FILE" "$pg:/tmp/cng-seed.sql"
docker exec -i "$pg" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cng -f /tmp/cng-seed.sql >/dev/null
seeded_feature_count="$(docker exec "$pg" psql -tA -U postgres -d honua_cng -c 'SELECT COUNT(*) FROM public.features WHERE layer_id=1000')"
printf '%s\n' "$seeded_feature_count" > "$OUT/expected-feature-count.txt"
compose up -d --no-build honua-server
wait_healthy honua-server 300

path="/rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet"
code="$(curl -sS --retry 3 --retry-all-errors -o "$OUT/cng.parquet" -w '%{http_code}' "http://localhost:${PORT}${path}")" \
  || fail "request failed (server crashed or refused the connection)"
bytes="$(wc -c < "$OUT/cng.parquet")"
head_magic="$(head -c 4 "$OUT/cng.parquet")"
tail_magic="$(tail -c 4 "$OUT/cng.parquet")"
printf 'GET %s\nHTTP %s\nbytes %s\nhead %s\ntail %s\n' "$path" "$code" "$bytes" "$head_magic" "$tail_magic" > "$OUT/http.txt"
cat "$OUT/image.txt" "$OUT/http.txt"

[[ "$code" == 200 ]] || fail "f=parquet answered HTTP ${code}"
if [[ "$head_magic" != PAR1 || "$tail_magic" != PAR1 ]]; then
  fail "f=parquet body is not a Parquet file (${bytes} bytes): $(head -c 300 "$OUT/cng.parquet" | tr -cd '[:print:]')"
fi
path="/rest/services/cng/MapServer/export?bbox=-180,-90,180,90&bboxSR=4326&size=256,256&format=png&f=image"
code="$(curl -sS --retry 3 --retry-all-errors -o "$OUT/cng.png" -w '%{http_code}' "http://localhost:${PORT}${path}")" \
  || fail "render request failed"
[[ "$code" == 200 ]] || fail "map export answered HTTP ${code}"
python3 - "$OUT/cng.png" <<'PYPNG'
from pathlib import Path
import sys
if Path(sys.argv[1]).read_bytes()[:8] != b'\x89PNG\r\n\x1a\n':
    raise SystemExit('Map export did not produce a PNG')
PYPNG
admin_key="$(compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["services"]["honua-server"]["environment"]["HONUA_ADMIN_PASSWORD"])')"
for format in gpkg shapefile; do
  case "$format" in
    gpkg) fixture=survey-sites.gpkg ;;
    shapefile) fixture=survey-sites-shapefile.zip ;;
  esac
  code="$(curl -sS --max-time 120 -o "$OUT/import-$format.json" -w '%{http_code}' \
    -H "X-API-Key: $admin_key" \
    -F "File=@$REPO_ROOT/tests/fixtures/external-format-corpus/v1/$fixture" \
    -F "TableName=smoke_reader_$format" -F OverwriteExisting=true \
    "http://localhost:${PORT}/api/v1/admin/import/upload")" || fail "$format import request failed"
  [[ "$code" == 200 ]] || fail "$format import answered HTTP ${code}"
  "$format_python" "$format_assertions" import "$OUT/import-$format.json" --feature-count 3
done
path="/api/v1/admin/services/cng/layers/1000/export?format=gpkg"
code="$(curl -sS --max-time 120 -H "X-API-Key: $admin_key" -o "$OUT/cng.gpkg" -w '%{http_code}' "http://localhost:${PORT}${path}")" \
  || fail "GeoPackage export failed"
[[ "$code" == 200 ]] || fail "GeoPackage export answered HTTP ${code}"
"$format_python" "$format_assertions" gpkg "$OUT/cng.gpkg" --feature-count "$seeded_feature_count"
path="/rest/services/cng/MapServer/WMS?SERVICE=WMS&REQUEST=GetLegendGraphic&VERSION=1.3.0&LAYER=1000&FORMAT=image/png&WIDTH=24&HEIGHT=24&TRANSPARENT=TRUE"
code="$(curl -sS --max-time 120 -o "$OUT/legend.png" -w '%{http_code}' "http://localhost:${PORT}${path}")" \
  || fail "legend request failed"
[[ "$code" == 200 ]] || fail "legend answered HTTP ${code}"
"$format_python" "$format_assertions" legend "$OUT/legend.png"
# The container must still be serving after the encoder ran: a native crash exits the process.
wait_healthy honua-server 60
verify_persisted_store_refusal
echo "Serving format smoke passed: ${bytes} bytes of GeoParquet from ${IMAGE}"
