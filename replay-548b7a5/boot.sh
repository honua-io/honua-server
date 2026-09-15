#!/usr/bin/env bash
# Isolated Production boot of the pinned 2026.1 candidate for the #4577 replay.
# Own network/subnet, no published host ports, never touches honua-esri-compat.
set -euo pipefail

IMAGE="${IMAGE:-ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1}"
P=c4577
NET=${P}-net
SUBNET=172.31.253.0/24
CADDY_IP=172.31.253.10
HOST=candidate.honua.test
ADMIN_PASSWORD="${ADMIN_PASSWORD:?set ADMIN_PASSWORD}"
here="$(cd "$(dirname "$0")" && pwd)"

docker network inspect "$NET" >/dev/null 2>&1 || docker network create --subnet "$SUBNET" "$NET" >/dev/null

docker run -d --name ${P}-pg --network "$NET" \
  -e POSTGRES_USER=honua_user -e POSTGRES_PASSWORD=honua_password -e POSTGRES_DB=honua \
  postgis/postgis:16-3.4 >/dev/null
docker run -d --name ${P}-redis --network "$NET" redis:7-alpine >/dev/null

for _ in $(seq 1 90); do
  docker logs ${P}-pg 2>&1 | grep -q 'PostgreSQL init process complete' && \
    docker exec ${P}-pg pg_isready -U honua_user -d honua >/dev/null 2>&1 && break
  sleep 2
done
docker exec ${P}-pg psql -v ON_ERROR_STOP=1 -U honua_user -d honua \
  -c 'CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster;' >/dev/null

docker run -d --name ${P}-honua --network "$NET" \
  -e ConnectionStrings__DefaultConnection="Host=${P}-pg;Database=honua;Username=honua_user;Password=honua_password" \
  -e ConnectionStrings__Redis="${P}-redis:6379" \
  -e HONUA_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  -e HostValidation__AllowedHosts__0="$HOST" \
  -e HostValidation__AllowedHosts__1="${P}-honua" \
  -e Security__ConnectionEncryption__MasterKey="c4577-replay-master-key-0123456789abcdef0123456789" \
  -e Security__ConnectionEncryption__Salt="MDEyMzQ1Njc4OWFiY2RlZg==" \
  -e Kestrel__Endpoints__Http__Url="http://+:8080" \
  -e PUBLIC_BASE_URL="https://${HOST}:8443" \
  -e ForwardedHeaders__Enabled=true \
  -e ForwardedHeaders__ForwardLimit=2 \
  -e ForwardedHeaders__KnownProxies__0="$CADDY_IP" \
  -e Authentication__PortalToken__OAuth2__EnableClientCredentials="${ENABLE_CLIENT_CREDENTIALS:-true}" \
  -e Operations__SecretChannel__KeyRingCertificatePath=/keyring/keyring.pfx \
  -v "$here/keyring/keyring.pfx:/keyring/keyring.pfx:ro" \
  "$IMAGE" >/dev/null

cat > "$here/Caddyfile" <<EOF
{
  admin off
}
${HOST}:8443 {
  tls internal
  reverse_proxy ${P}-honua:8080
}
EOF
docker run -d --name ${P}-tls --network "$NET" --ip "$CADDY_IP" --network-alias "$HOST" \
  -v "$here/Caddyfile:/etc/caddy/Caddyfile:ro" caddy:2 >/dev/null

for i in $(seq 1 60); do
  code=$(docker run --rm --network "$NET" python:3.12-alpine python -c \
    "import urllib.request as u,sys
try: print(u.urlopen(u.Request('http://${P}-honua:8080/healthz/ready',headers={'Host':'${P}-honua'}),timeout=5).status)
except Exception as e: print(getattr(e,'code','ERR'))" 2>/dev/null || true)
  echo "ready probe $i: $code"
  [ "$code" = "200" ] && exit 0
  if [ "$(docker inspect -f '{{.State.Running}}' ${P}-honua)" != "true" ]; then
    docker logs --tail 60 ${P}-honua; exit 1
  fi
  sleep 3
done
docker logs --tail 60 ${P}-honua
exit 1
