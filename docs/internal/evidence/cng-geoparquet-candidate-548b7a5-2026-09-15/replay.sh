#!/usr/bin/env bash
# honua-server#4747 replay: boot a digest-addressed honua-server image through the CNG lane's own
# docker/cng/compose.yml, in the same order as scripts/conformance/cng/run-cng-conformance.sh
# (migrate, stop, seed docker/cng/seed.sql, restart), then fetch the live seeded layer with
# f=parquet. Keeps the HTTP status, response body, image identity and server log. The only change
# from the lane is the image reference and a private compose project name and ports, so parallel
# stacks on a shared Docker engine are never touched.
#
# usage: replay.sh <image@sha256:...> <output-dir> [server-port] [postgres-port]
set -euo pipefail

IMAGE="${1:?digest-addressed image required}"
OUT="${2:?output directory required}"
PORT="${3:-18747}"
PG_PORT="${4:-18748}"
[[ "$IMAGE" =~ @sha256:[0-9a-f]{64}$ ]] || { echo "image must be digest-addressed" >&2; exit 2; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/cng/compose.yml"
SEED_FILE="$REPO_ROOT/docker/cng/seed.sql"
PROJECT="honua-4747-${IMAGE: -12}"
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
cleanup() { compose down --remove-orphans --volumes >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

wait_healthy() {
  local svc="$1" deadline=$((SECONDS + $2)) id
  id="$(compose ps -q "$svc")"
  until [[ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" == healthy ]]; do
    if (( SECONDS > deadline )); then echo "$svc not healthy" >&2; compose logs --no-color "$svc" | tail -50 >&2; return 1; fi
    sleep 5
  done
}

docker image inspect "$IMAGE" \
  --format 'image={{index .RepoDigests 0}}{{"\n"}}revision={{index .Config.Labels "org.opencontainers.image.revision"}}{{"\n"}}architecture={{.Architecture}}' \
  > "$OUT/image.txt"

compose up -d --no-build postgres redis
wait_healthy postgres 120
wait_healthy redis 60
compose up -d --no-build honua-server
wait_healthy honua-server 300
compose stop honua-server
pg="$(compose ps -q postgres)"
docker cp "$SEED_FILE" "$pg:/tmp/cng-seed.sql"
docker exec -i "$pg" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cng -f /tmp/cng-seed.sql >/dev/null
compose up -d --no-build honua-server
wait_healthy honua-server 300

url="http://localhost:${PORT}/rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet"
code="$(curl -sS -o "$OUT/cng.parquet" -w '%{http_code}' "$url")"
printf 'GET %s\nHTTP %s\nbytes %s\n' "${url#http://localhost:${PORT}}" "$code" "$(wc -c < "$OUT/cng.parquet")" > "$OUT/http.txt"
compose logs --no-color honua-server > "$OUT/honua-server.log" 2>&1 || true
cat "$OUT/image.txt" "$OUT/http.txt"
