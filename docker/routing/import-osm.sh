#!/usr/bin/env bash
#
# import-osm.sh — import a real OpenStreetMap extract into the Honua pgRouting
# development database using osm2pgrouting (issue #1266).
#
# By default the routing DB (docker/routing/compose.yml) is seeded with a tiny
# deterministic 3x3 lattice (init-routing.sql) so tests and quick demos work
# offline. This script provides the OTHER path: ingesting a real OSM street
# network so routes/service areas reflect actual roads.
#
# osm2pgrouting builds its OWN osm2pgrouting-compatible `ways` /
# `ways_vertices_pgr` schema. The --clean flag DROPS and recreates those
# tables, so running this script REPLACES the lattice seed in the target DB.
#
# Defaults match docker/routing/compose.yml (db honua_routing, user/pass
# test/test, host localhost, port 5434). Override via flags or PG* env vars.
#
# Prerequisites and the open-data / no-proprietary-data constraint (ADR-0050)
# are documented in docker/routing/README.md.
#
# Usage:
#   docker/routing/import-osm.sh                       # bundled synthetic sample
#   docker/routing/import-osm.sh --file my-city.osm.pbf
#   PGPORT=15434 docker/routing/import-osm.sh --file extract.osm --conf custom.xml
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

# --- Defaults (match docker/routing/compose.yml) ----------------------------
FILE="${SCRIPT_DIR}/sample/sample.osm"
CONF="${SCRIPT_DIR}/mapconfig.xml"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5434}"
PGDATABASE="${PGDATABASE:-honua_routing}"
PGUSER="${PGUSER:-test}"
PGPASSWORD="${PGPASSWORD:-test}"

usage() {
    cat <<'EOF'
Import an OSM extract into the Honua pgRouting dev DB via osm2pgrouting.

Options:
  --file PATH    OSM extract (.osm/.osm.xml/.pbf). Default: bundled sample.
  --conf PATH    osm2pgrouting tag config. Default: docker/routing/mapconfig.xml
  --host HOST    DB host  (env PGHOST,     default localhost)
  --port PORT    DB port  (env PGPORT,     default 5434)
  --dbname NAME  Database (env PGDATABASE, default honua_routing)
  --username U   User     (env PGUSER,     default test)
  --password P   Password (env PGPASSWORD, default test)
  -h, --help     Show this help.

WARNING: uses osm2pgrouting --clean, which REPLACES the lattice seed in the
target database. See docker/routing/README.md.
EOF
}

# --- Parse flags ------------------------------------------------------------
while [ "$#" -gt 0 ]; do
    case "$1" in
        --file)     FILE="$2";       shift 2 ;;
        --conf)     CONF="$2";       shift 2 ;;
        --host)     PGHOST="$2";     shift 2 ;;
        --port)     PGPORT="$2";     shift 2 ;;
        --dbname)   PGDATABASE="$2"; shift 2 ;;
        --username) PGUSER="$2";     shift 2 ;;
        --password) PGPASSWORD="$2"; shift 2 ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# --- Preflight: osm2pgrouting present? --------------------------------------
if ! command -v osm2pgrouting >/dev/null 2>&1; then
    cat >&2 <<EOF
error: 'osm2pgrouting' is not installed or not on PATH.

Install it, then re-run this script:
  - Debian/Ubuntu:  sudo apt-get install osm2pgrouting
  - Or run it from a container that bundles osm2pgrouting.

See docker/routing/README.md ("Real OSM ingestion") for details, including
how to point it at the docker/routing/compose.yml database.
EOF
    exit 127
fi

# --- Preflight: input files exist -------------------------------------------
if [ ! -f "$FILE" ]; then
    echo "error: OSM extract not found: $FILE" >&2
    exit 1
fi
if [ ! -f "$CONF" ]; then
    echo "error: mapconfig not found: $CONF" >&2
    exit 1
fi

echo "Importing OSM extract into pgRouting DB:"
echo "  file:     $FILE"
echo "  conf:     $CONF"
echo "  database: $PGDATABASE @ $PGHOST:$PGPORT (user $PGUSER)"
echo "  NOTE: --clean REPLACES any existing lattice seed in $PGDATABASE."

osm2pgrouting \
    --f "$FILE" \
    --conf "$CONF" \
    --dbname "$PGDATABASE" \
    --host "$PGHOST" \
    --port "$PGPORT" \
    --username "$PGUSER" \
    --password "$PGPASSWORD" \
    --clean

echo "Done. Inspect the imported topology, e.g.:"
echo "  psql \"host=$PGHOST port=$PGPORT dbname=$PGDATABASE user=$PGUSER password=$PGPASSWORD\" \\"
echo "    -c 'SELECT count(*) AS ways FROM ways; SELECT count(*) AS vertices FROM ways_vertices_pgr;'"
