#!/usr/bin/env bash
# Candidate-bound replay for honua-server#4783 on a pinned image digest.
#
# Boots an isolated PostGIS + Redis + honua-server stack from the image digest,
# seeds honua-esri-compat's role-restricted authz fixture (arcgis_compat_protected
# allowedRoles=[admin], arcgis_compat_scoped allowedRoles=[scoped-api-key],
# browser_compat anonymous), runs the per-principal catalog/handoff probe and
# tears everything down. The transcript carries no credentials.
#
#   HONUA_SERVER_IMAGE=ghcr.io/honua-io/honua-server@sha256:... \
#   HONUA_SERVER_REVISION=<40-char sha> \
#   ESRI_COMPAT_REF=<honua-esri-compat commit> \
#   ./catalog-authority-548b7a5-2026-09-15-replay.sh <transcript.json>
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
server_repo="$(git -C "$here" rev-parse --show-toplevel)"
compat_repo="${ESRI_COMPAT_REPO:-$server_repo/../honua-esri-compat}"
image="${HONUA_SERVER_IMAGE:?image digest reference required}"
revision="${HONUA_SERVER_REVISION:?source revision required}"
compat_ref="${ESRI_COMPAT_REF:?honua-esri-compat commit required}"
out="${1:?transcript path required}"
lane="${REPLAY_LANE:-h4783-replay}"
port="${REPLAY_PORT:-18780}"
work="$(mktemp -d)"
net="$lane-net"

cleanup() {
  docker rm -f "$lane-server" "$lane-postgres" "$lane-redis" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
cleanup
work="$(mktemp -d)"
log() { printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }

# Throwaway secrets: Production requires a complex admin password and, with Redis,
# a key-ring PKCS#12 (#4722).
admin_password="R$(openssl rand -hex 12)a!9"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/k.key" -out "$work/k.crt" \
  -days 1 -subj "/CN=$lane-keyring" 2>/dev/null
openssl pkcs12 -export -inkey "$work/k.key" -in "$work/k.crt" -out "$work/keyring.pfx" -passout pass:
chmod 644 "$work/keyring.pfx"

git -C "$compat_repo" show "$compat_ref:docker/seed/arcgis-compat.sql" > "$work/arcgis-compat.sql"
git -C "$server_repo" show "$revision:tests/seed/base-schema.sql" > "$work/base-schema.sql"
python3 - "$work/base-schema.sql" > "$work/compile-v2.sql" <<'PY'
import sys
src = open(sys.argv[1]).read()
name = "seed_metadata_v2_compat_snapshot"
start = src.index("CREATE OR REPLACE FUNCTION honua." + name)
b1 = src.index("$$", start); b2 = src.index("$$", b1 + 2)
print(src[start:src.index(";", b2) + 1])
print("SELECT honua." + name + "();")
PY

log "starting PostGIS and Redis"
docker network create "$net" >/dev/null
docker run -d --name "$lane-postgres" --network "$net" \
  -e POSTGRES_USER=honua_user -e POSTGRES_PASSWORD=honua_password -e POSTGRES_DB=honua_dev \
  postgis/postgis:16-3.4 >/dev/null
docker run -d --name "$lane-redis" --network "$net" redis:7-alpine >/dev/null
for _ in $(seq 1 60); do
  # TCP only: the image's init-phase server listens on the Unix socket alone and
  # restarts once, so a socket probe admits the server into that restart window.
  docker exec "$lane-postgres" pg_isready -h 127.0.0.1 -U honua_user -d honua_dev >/dev/null 2>&1 && break
  sleep 2
done

log "starting $image"
docker run -d --name "$lane-server" --network "$net" -p "127.0.0.1:$port:8080" \
  -v "$work/keyring.pfx:/keyring/keyring.pfx:ro" \
  -e ConnectionStrings__DefaultConnection="Host=$lane-postgres;Database=honua_dev;Username=honua_user;Password=honua_password" \
  -e ConnectionStrings__Redis="$lane-redis:6379" \
  -e HONUA_ADMIN_PASSWORD="$admin_password" \
  -e Operations__SecretChannel__KeyRingCertificatePath=/keyring/keyring.pfx \
  -e Security__ConnectionEncryption__MasterKey=local-dev-master-key-not-for-production-0123456789 \
  -e Security__ConnectionEncryption__Salt=MDEyMzQ1Njc4OWFiY2RlZg== \
  -e Licensing__Mode=Disabled \
  -e Kestrel__Endpoints__Http__Url=http://+:8080 \
  -e HostValidation__AllowedHosts__0=localhost \
  -e PUBLIC_BASE_URL="http://localhost:$port" \
  "$image" >/dev/null

wait_ready() {
  log "waiting for readiness"
  for _ in $(seq 1 90); do
    if [ "$(curl -s --max-time 5 -o /dev/null -w '%{http_code}' -H "Host: localhost" "http://127.0.0.1:$port/healthz/ready")" = 200 ]; then
      return 0
    fi
    sleep 2
  done
  docker logs --tail 80 "$lane-server" >&2
  return 1
}
wait_ready
log "ready; seeding authz fixture"

psql_run() { docker exec -i "$lane-postgres" psql -X -q -v ON_ERROR_STOP=1 -U honua_user -d honua_dev; }
psql_run < "$work/arcgis-compat.sql" >/dev/null
# First boot activates an empty Production Metadata v2 snapshot, which masks V1
# rows inserted afterwards; compile the seeded catalog into v2 and restart.
psql_run < "$work/compile-v2.sql" >/dev/null
docker restart "$lane-server" >/dev/null
wait_ready
log "restarted on seeded snapshot; probing"

running_image="$(docker inspect "$lane-server" --format '{{.Image}}')"
running_revision="$(docker inspect "$lane-server" --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')"

REPLAY_ORIGIN="http://localhost:$port" \
REPLAY_CONNECT="127.0.0.1:$port" \
REPLAY_BOOTSTRAP_KEY="$admin_password" \
REPLAY_IMAGE_ID="$running_image" \
REPLAY_IMAGE_REVISION="$running_revision" \
REPLAY_EXPECTED_REVISION="$revision" \
REPLAY_IMAGE_REF="$image" \
REPLAY_COMPAT_REF="$compat_ref" \
REPLAY_SEED_SHA256="$(sha256sum "$work/arcgis-compat.sql" | cut -d' ' -f1)" \
  python3 "$here/catalog-authority-548b7a5-2026-09-15-probe.py" "$out"
