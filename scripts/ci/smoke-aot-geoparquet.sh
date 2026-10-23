#!/usr/bin/env bash
# honua-server#4912 image-build regression: prove a built server image can load its GeoParquet
# encoder (ParquetSharpNative) by serving real GeoParquet bytes, before the image is published.
#
# The published musl native-AOT image passed the serving-image boundary check and every unit lane
# while each FeatureServer f=parquet answered the typed 501, because the glibc-only native library
# could not load. Only a request against the built image exposes that, so this boots the image
# through the CNG lane's own docker/cng/compose.yml in the lane's order (migrate, stop, seed
# docker/cng/seed.sql, restart) and fetches the seeded layer with f=parquet. It fails unless the
# response is HTTP 200 and the body is framed by the Parquet magic `PAR1` at both ends. A private
# compose project name and ports keep parallel stacks on a shared Docker engine untouched.
#
# usage: smoke-aot-geoparquet.sh <image> [output-dir] [server-port] [postgres-port]
set -euo pipefail

IMAGE="${1:?image reference required (tag or image@sha256:...)}"
OUT="${2:-$(mktemp -d)}"
PORT="${3:-18912}"
PG_PORT="${4:-18913}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/cng/compose.yml"
SEED_FILE="$REPO_ROOT/docker/cng/seed.sql"
PROJECT="honua-aot-geoparquet-smoke-$$"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

work="$(mktemp -d)"
: > "$work/empty-secret"
cat > "$work/override.yml" <<EOF
services:
  honua-server:
    image: $IMAGE
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

fail() {
  echo "::error::AOT GeoParquet smoke failed for ${IMAGE}: $*" >&2
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
# The container must still be serving after the encoder ran: a native crash exits the process.
wait_healthy honua-server 60
echo "AOT GeoParquet smoke passed: ${bytes} bytes of GeoParquet from ${IMAGE}"
