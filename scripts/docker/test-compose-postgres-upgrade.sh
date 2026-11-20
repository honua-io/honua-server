#!/usr/bin/env bash
# Upgrade proof for docker/compose-postgres. A volume created on the previous Compose database
# base (Debian 11, glibc 2.31) must start on the new image with every database's collation
# version refreshed after its text indexes are rebuilt (spatial ones untouched), with the password
# in a secret file only root can read, PostGIS and pgRouting unchanged, the real server as PID 1,
# and no second refresh on a restart. A fresh volume must initialise.
# Usage: test-compose-postgres-upgrade.sh <new image>
set -Eeuo pipefail

new_image="${1:?usage: test-compose-postgres-upgrade.sh <new image>}"
previous_image='pgrouting/pgrouting:17-3.5-3.7.3@sha256:5e6767abbd1fd9ead84c9988aa31bac71ca292ef8a1fc2e4a5ee2b613ac6a7bb'
suffix="$$-$RANDOM"
container="honua-db-upgrade-$suffix"
upgraded_volume="honua-db-upgrade-$suffix"
fresh_volume="honua-db-fresh-$suffix"
secrets="$(mktemp -d)"
printf 'upgrade-proof' > "$secrets/postgres_password"
chmod 0600 "$secrets/postgres_password"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm -f "$upgraded_volume" "$fresh_volume" >/dev/null 2>&1 || true
  rm -rf "$secrets"
}
trap cleanup EXIT

fail() {
  echo "::error::$*" >&2
  docker logs "$container" 2>&1 | tail -40 >&2 || true
  exit 1
}

# The installs mount the password as a file only root can read; the entrypoint reads it as root.
start() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker run -d --name "$container" -v "$2:/var/lib/postgresql/data" \
    -v "$secrets/postgres_password:/run/secrets/postgres_password:ro" \
    -e POSTGRES_USER=honua -e POSTGRES_DB=honua -e POSTGRES_PASSWORD_FILE=/run/secrets/postgres_password "$1" >/dev/null
}

relation_file() {
  sql "SELECT relfilenode FROM pg_class WHERE relname = '$1'"
}

# The Compose healthcheck: the real server (PID 1), not the entrypoint's temporary one.
wait_ready() {
  for _ in $(seq 1 120); do
    if docker exec "$container" sh -c \
      'test "$(head -n 1 /var/lib/postgresql/data/postmaster.pid 2>/dev/null)" = 1 && pg_isready -U honua -d honua' \
      >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  fail "database did not become ready"
}

sql() {
  docker exec "$container" psql --no-psqlrc --quiet -v ON_ERROR_STOP=1 -U honua -d honua -At -c "$1"
}

expect() {
  [ "$2" = "$3" ] || fail "$1: expected '$3', got '$2'"
  echo "ok: $1 = $3"
}

start "$previous_image" "$upgraded_volume"
wait_ready
sql "CREATE EXTENSION postgis; CREATE EXTENSION pgrouting;
     CREATE TABLE places(name text, location geometry(Point, 4326));
     CREATE INDEX places_name ON places(name); CREATE INDEX places_location ON places USING gist(location);
     INSERT INTO places SELECT md5(i::text) || chr(65 + i % 50), ST_MakePoint(i % 180, i % 90)
     FROM generate_series(1, 5000) i;" >/dev/null
previous_version="$(sql "SELECT datcollversion FROM pg_database WHERE datname = 'honua'")"
text_index_file="$(relation_file places_name)"
spatial_index_file="$(relation_file places_location)"

start "$new_image" "$upgraded_volume"
wait_ready
current_version="$(sql "SELECT pg_database_collation_actual_version(oid) FROM pg_database WHERE datname = 'honua'")"
[ "$previous_version" != "$current_version" ] || fail "the base change no longer changes the collation version ($current_version)"
expect "databases with a stale collation version" \
  "$(sql "SELECT count(*) FROM pg_database WHERE datallowconn AND datcollversion IS DISTINCT FROM pg_database_collation_actual_version(oid)")" 0
[ "$(relation_file places_name)" != "$text_index_file" ] || fail "the text index was not rebuilt"
expect "spatial index storage (not rebuilt)" "$(relation_file places_location)" "$spatial_index_file"
# bt_index_check raises an error when the index order disagrees with the collation.
expect "text index check" "$(sql "CREATE EXTENSION amcheck; SELECT 1 FROM (SELECT bt_index_check('places_name', true)) checked")" 1
expect "extension versions" \
  "$(sql "SELECT string_agg(extname || '=' || extversion, ',' ORDER BY extname) FROM pg_extension WHERE extname IN ('postgis', 'pgrouting')")" \
  "pgrouting=3.7.3,postgis=3.5.2"
expect "PID 1" "$(docker exec "$container" cat /proc/1/comm)" postgres

docker restart "$container" >/dev/null
wait_ready
expect "refreshes after a restart" "$(docker logs "$container" 2>&1 | grep -c '^honua: rebuilding text indexes in database honua ')" 1

start "$new_image" "$fresh_volume"
wait_ready
expect "fresh volume extensions" \
  "$(sql "CREATE EXTENSION postgis; CREATE EXTENSION postgis_raster; CREATE EXTENSION pgrouting; SELECT count(*) FROM pg_extension WHERE extname IN ('postgis', 'postgis_raster', 'pgrouting')")" 3
