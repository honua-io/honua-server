#!/usr/bin/env bash
# Recreate the owned GPServer desktop fixture (honua-server#4614, #4975) on one honua-server image.
#
#   recreate-fixture.sh sha256:<index digest of an imaged trunk nightly>
#
# The fixture tracks the newest imaged trunk nightly (operator ruling A, 2026-09-16). The digest is
# the only input. The script:
#   1. pulls ghcr.io/honua-io/honua-server@<digest>;
#   2. ensures the fixture Postgres runs with POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL and
#      POSTGIS_ENABLE_OUTDB_RASTERS=1 (recreating it on the SAME data volume when either is missing);
#   3. recreates gpserver-4614-4616-server on the digest with the container's explicitly-set
#      environment only (image defaults such as HONUA_GIT_SHA are never copied forward), the same
#      key-ring bind mount, port and networks;
#   4. starts the private Redis, the Caddy TLS proxy and the tracer relay (never recreated);
#   5. runs the readiness check (ready-check.sh) and exits non-zero unless every assertion passes.
#
# Secret environment values are copied through a 0600 file outside the repository and never printed.
# It never touches honua-esri-compat-* containers, networks or volumes and never removes a volume.
set -euo pipefail

digest="${1:-}"
if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "usage: $0 sha256:<64 hex>  (the bare image index digest, no ghcr.io/...@ prefix)" >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
evidence_root="${GP_FIXTURE_EVIDENCE_ROOT:-/home/mike/honua-io/gpserver-4614-4616-evidence}"
state_dir="$evidence_root/recreate"
image="ghcr.io/honua-io/honua-server@$digest"

server=gpserver-4614-4616-server
postgres=gpserver-4614-4616-postgres
redis=gpserver-4614-548b7a5-redis
tls=gpserver-4614-4616-tls
tracer=gpserver-4614-4616-tracer
net=gpserver-4614-4616
tls_net=gpserver-4614-4616-tls
pg_volume=gpserver-4614-4616-pgdata
pg_image=postgis/postgis:16-3.4
keyring="$evidence_root/diagnostic-keyring/keyring.pfx"

for name in "$server" "$postgres" "$redis" "$tls" "$tracer" "$net" "$tls_net" "$pg_volume"; do
  [[ "$name" == honua-esri-compat* ]] && { echo "refusing to touch $name" >&2; exit 2; }
done

umask 077
mkdir -p "$state_dir"

echo "== pull $image"
docker pull -q "$image" >/dev/null
[[ "$(docker image inspect "$image" --format '{{.Id}}')" == "$digest" ]] \
  || { echo "pulled image id does not equal $digest" >&2; exit 1; }

echo "== postgres ($postgres): PostGIS GDAL drivers"
pg_env="$(docker inspect "$postgres" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null || true)"
if grep -qx 'POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL' <<<"$pg_env" \
  && grep -qx 'POSTGIS_ENABLE_OUTDB_RASTERS=1' <<<"$pg_env"; then
  echo "   already set; starting"
  docker start "$postgres" >/dev/null
else
  docker volume inspect "$pg_volume" >/dev/null
  if docker inspect "$postgres" >/dev/null 2>&1; then
    mounted="$(docker inspect "$postgres" --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}')"
    [[ "$mounted" == "$pg_volume" ]] || { echo "$postgres does not mount $pg_volume (found '$mounted')" >&2; exit 1; }
    docker stop -t 30 "$postgres" >/dev/null || true
    docker rm "$postgres" >/dev/null
  fi
  echo "   recreating on volume $pg_volume (data preserved) with the GDAL driver variables"
  docker run -d --name "$postgres" \
    --network "$net" --network-alias postgres \
    -v "$pg_volume:/var/lib/postgresql/data" \
    -e POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL \
    -e POSTGIS_ENABLE_OUTDB_RASTERS=1 \
    "$pg_image" >/dev/null
fi
for _ in $(seq 1 60); do
  docker exec "$postgres" pg_isready -q >/dev/null 2>&1 && break
  sleep 2
done
docker exec "$postgres" pg_isready -q || { echo "$postgres is not accepting connections" >&2; exit 1; }

echo "== redis ($redis)"
docker start "$redis" >/dev/null

echo "== server ($server) -> $image"
env_file="$state_dir/server-explicit.env"
if docker inspect "$server" >/dev/null 2>&1; then
  old_image_id="$(docker inspect "$server" --format '{{.Image}}')"
  # Explicitly-set environment = container env minus the lines its image supplies.
  comm -23 \
    <(docker inspect "$server" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed '/^$/d' | sort) \
    <(docker image inspect "$old_image_id" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed '/^$/d' | sort) \
    > "$env_file.new"
  grep -q '^ConnectionStrings__DefaultConnection=' "$env_file.new" \
    || { echo "explicit env of $server lacks ConnectionStrings__DefaultConnection; refusing" >&2; exit 1; }
  mv "$env_file.new" "$env_file"
  echo "   captured $(wc -l <"$env_file") explicitly-set variables (values not printed)"
  docker stop -t 30 "$server" >/dev/null || true
  docker rm "$server" >/dev/null
else
  [[ -s "$env_file" ]] || { echo "$server is absent and no saved $env_file exists" >&2; exit 1; }
  echo "   $server absent; reusing saved explicit environment"
fi
[[ -f "$keyring" ]] || { echo "missing key ring $keyring" >&2; exit 1; }
docker create --name "$server" \
  --env-file "$env_file" \
  -v "$keyring:/keyring/keyring.pfx:ro" \
  -p 127.0.0.1:18164:8080 \
  --network "$net" --network-alias honua \
  "$image" >/dev/null
docker network connect --alias honua "$tls_net" "$server"
docker start "$server" >/dev/null

echo "== tls proxy ($tls) and tracer ($tracer)"
docker start "$tls" >/dev/null
docker start "$tracer" >/dev/null

echo "== wait for health"
for _ in $(seq 1 60); do
  status="$(docker inspect "$server" --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}')"
  [[ "$status" == "running healthy" ]] && break
  [[ "$status" == exited* ]] && { docker logs --tail 40 "$server" 2>&1 | grep -v -i -E 'password|secret|masterkey|connectionstring' >&2; exit 1; }
  sleep 5
done
echo "   $status"

exec "$here/ready-check.sh" "$digest"
