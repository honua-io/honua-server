#!/usr/bin/env bash
# honua-server#4912 criteria 1 and 2 on the pinned candidate: serve FeatureServer f=parquet from the
# published native-AOT image while it runs in the posture the product ships in docker-compose.yml —
# non-root (USER 1001 from docker/Dockerfile.aot), read-only root filesystem, all capabilities
# dropped, no-new-privileges — and record the runtime posture the container actually ran with.
#
# The lane's own docker/cng/compose.yml is used unchanged except for an override that pins the image
# (optionally for a foreign platform, so the arm64 leg of the AOT manifest can be replayed under
# binfmt/qemu), applies the hardening, and gives the stack a private project name and ports.
#
# usage: hardened-replay.sh <image> <out-dir> <server-port> <postgres-port> [platform]
set -euo pipefail

IMAGE="${1:?image reference required}"
OUT="${2:?output directory required}"
PORT="${3:?server port required}"
PG_PORT="${4:?postgres port required}"
PLATFORM="${5:-}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/cng/compose.yml"
SEED_FILE="$REPO_ROOT/docker/cng/seed.sql"
PROJECT="honua-4912-hardened-$$"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"

work="$(mktemp -d)"
: > "$work/empty-secret"
{
  echo "services:"
  echo "  honua-server:"
  echo "    image: $IMAGE"
  [[ -n "$PLATFORM" ]] && echo "    platform: $PLATFORM"
  cat <<'YAML'
    read_only: true
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    tmpfs:
      - /tmp:nosuid,size=256m
      - /var/lib/honua/storage:nosuid,size=64m
YAML
} > "$work/override.yml"

export HONUA_CNG_SERVER_PORT="$PORT" HONUA_CNG_POSTGRES_PORT="$PG_PORT"
export HONUA_GITHUB_ACTOR_SECRET_FILE="$work/empty-secret" HONUA_GITHUB_TOKEN_SECRET_FILE="$work/empty-secret"
compose() { docker compose -p "$PROJECT" -f "$COMPOSE_FILE" -f "$work/override.yml" "$@"; }
cleanup() {
  compose logs --no-color honua-server > "$OUT/honua-server.log" 2>&1 || true
  compose down --remove-orphans --volumes >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
fail() { echo "FAILED: $*" >&2; compose logs --no-color --tail 60 honua-server >&2 || true; exit 1; }

wait_healthy() {
  local svc="$1" deadline=$((SECONDS + $2)) id
  id="$(compose ps -q "$svc")"
  until [[ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" == healthy ]]; do
    (( SECONDS > deadline )) && fail "$svc did not become healthy"
    sleep 5
  done
}

docker image inspect "$IMAGE" --format \
  'image={{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}{{"\n"}}revision={{index .Config.Labels "org.opencontainers.image.revision"}}{{"\n"}}architecture={{.Architecture}}{{"\n"}}image_user={{.Config.User}}{{"\n"}}entrypoint={{index .Config.Labels "honua.runtime.entrypoint"}}{{"\n"}}compilation={{index .Config.Labels "honua.runtime.compilation"}}' \
  > "$OUT/image.txt"

compose up -d --no-build postgres redis
wait_healthy postgres 240
wait_healthy redis 120
compose up -d --no-build honua-server
wait_healthy honua-server 900
compose stop honua-server
pg="$(compose ps -q postgres)"
docker cp "$SEED_FILE" "$pg:/tmp/cng-seed.sql"
docker exec -i "$pg" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cng -f /tmp/cng-seed.sql >/dev/null
compose up -d --no-build honua-server
wait_healthy honua-server 900

srv="$(compose ps -q honua-server)"
docker inspect "$srv" --format \
  'ReadonlyRootfs={{.HostConfig.ReadonlyRootfs}}{{"\n"}}CapDrop={{.HostConfig.CapDrop}}{{"\n"}}CapAdd={{.HostConfig.CapAdd}}{{"\n"}}SecurityOpt={{.HostConfig.SecurityOpt}}{{"\n"}}Privileged={{.HostConfig.Privileged}}{{"\n"}}ConfigUser={{.Config.User}}' \
  > "$OUT/runtime-posture.txt"
{
  echo "id -u/-g/whoami inside the running container:"
  docker exec "$srv" sh -c 'id -u; id -g; id' 2>&1 || echo "(no shell in image)"
  echo
  echo "root filesystem write attempt (must be refused):"
  docker exec "$srv" sh -c 'touch /app/write-probe 2>&1 || echo "refused: exit $?"' 2>&1 || true
  echo
  # Native GDAL/PROJ/GEOS libraries anywhere in the running root filesystem (must be none).
  # The authoritative scan is scripts/ci/verify-serving-image-boundary.py (see boundary/); this only
  # confirms the container that served the request is the same GDAL-free rootfs.
  echo "GDAL/PROJ/GEOS native libraries in the running container (must be none):"
  docker exec "$srv" sh -c 'find / -xdev -type f \( -iname "*gdal*.so*" -o -iname "*proj*.so*" -o -iname "libgeos*.so*" -o -iname "geos_c*.so*" \) 2>/dev/null | head -20 || true; echo "(end of list)"' 2>&1 || true
} >> "$OUT/runtime-posture.txt"

path="/rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet"
code="$(curl -sS --retry 3 --retry-all-errors --max-time 300 -o "$OUT/cng.parquet" -w '%{http_code}' "http://localhost:${PORT}${path}")" \
  || fail "request failed (server crashed or refused the connection)"
bytes="$(wc -c < "$OUT/cng.parquet")"
printf 'GET %s\nHTTP %s\nbytes %s\nhead %s\ntail %s\n' "$path" "$code" "$bytes" \
  "$(head -c 4 "$OUT/cng.parquet")" "$(tail -c 4 "$OUT/cng.parquet")" > "$OUT/http.txt"
cat "$OUT/image.txt" "$OUT/runtime-posture.txt" "$OUT/http.txt"

[[ "$code" == 200 ]] || fail "f=parquet answered HTTP ${code}"
[[ "$(head -c 4 "$OUT/cng.parquet")" == PAR1 && "$(tail -c 4 "$OUT/cng.parquet")" == PAR1 ]] \
  || fail "f=parquet body is not a Parquet file (${bytes} bytes)"
grep -q 'ReadonlyRootfs=true' "$OUT/runtime-posture.txt" || fail "container did not run with a read-only root filesystem"
# The process must still be alive after the native encoder ran: a native crash exits it.
wait_healthy honua-server 120
sha256sum "$OUT/cng.parquet" | sed "s|$OUT/||" > "$OUT/cng.parquet.sha256"
echo "hardened replay passed: ${bytes} bytes of GeoParquet from ${IMAGE}"
