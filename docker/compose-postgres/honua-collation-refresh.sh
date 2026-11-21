#!/usr/bin/env bash
# A newer C library can sort text differently from the one that built a database's indexes.
# PostgreSQL records the collation version per database and per collation. In every database
# where a recorded version differs from the running library, rebuild the indexes whose key
# columns use a collation other than C or POSIX (spatial and numeric indexes are left alone),
# then record the new versions. This runs on a temporary socket-only server before the real one
# starts, and any failure stops the start. template0 accepts no connections and holds no user
# indexes; a database cloned from it records the running library's version.
# Expects PGDATA and POSTGRES_USER, as prepared by honua-postgres-entrypoint.sh.
set -Eeuo pipefail
source /usr/local/bin/docker-entrypoint.sh
# Crash recovery of a large database can take longer than pg_ctl's default wait.
export PGCTLTIMEOUT="${PGCTLTIMEOUT:-3600}"

docker_temp_server_start "$@"
trap docker_temp_server_stop ERR

run_sql() {
  psql --no-psqlrc --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --no-password -At "$@"
}

# A plain assignment, so a failed listing stops the start instead of skipping the refresh.
database_list="$(run_sql --dbname postgres -c "SELECT datname FROM pg_database WHERE datallowconn ORDER BY datname")"
mapfile -t databases <<<"$database_list"
for database in "${databases[@]}"; do
  run_sql --dbname "$database" <<'SQL'
SELECT (datcollversion IS DISTINCT FROM pg_database_collation_actual_version(oid))
    OR EXISTS (SELECT FROM pg_collation c WHERE c.collprovider <> 'd'
               AND c.collversion IS DISTINCT FROM pg_collation_actual_version(c.oid)) AS stale
FROM pg_database WHERE datname = current_database()
\gset
\if :stale
\echo honua: rebuilding text indexes in database :DBNAME for the current collation version
SELECT format('REINDEX INDEX %s', i.indexrelid::regclass)
FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname NOT IN ('pg_catalog', 'pg_toast', 'information_schema')
  AND EXISTS (SELECT FROM unnest(i.indcollation::oid[]) AS k(collation_oid)
              WHERE k.collation_oid <> 0
                AND k.collation_oid NOT IN ('"C"'::regcollation::oid, '"POSIX"'::regcollation::oid))
ORDER BY 1
\gexec
SELECT format('ALTER COLLATION %I.%I REFRESH VERSION', n.nspname, c.collname)
FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace
WHERE c.collprovider <> 'd' AND c.collversion IS DISTINCT FROM pg_collation_actual_version(c.oid)
\gexec
SELECT format('ALTER DATABASE %I REFRESH COLLATION VERSION', current_database())
WHERE (SELECT datcollversion IS DISTINCT FROM pg_database_collation_actual_version(oid)
       FROM pg_database WHERE datname = current_database())
\gexec
\endif
SQL
done
trap - ERR
docker_temp_server_stop
