#!/usr/bin/env bash
# Applies the canonical client-compat seed bundle to a freshly-started
# postgres so honua + lane services see the expected services and layers:
#   - tests/seed/client-compat-v1.sql → schema + `test_service` (layer 0)
#     used by the pyqgis lane
#   - tests/seed/browser-compat.yaml  → `browser_compat` (layers 2000-2002)
#     used by the cesium and openlayers lanes
# Connection parameters arrive via PGHOST/PGUSER/PGPASSWORD/PGDATABASE env.
set -euo pipefail

: "${PGHOST:?PGHOST is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSWORD:?PGPASSWORD is required}"
: "${PGDATABASE:?PGDATABASE is required}"
: "${PGPORT:=5432}"

export PGHOST PGUSER PGPASSWORD PGDATABASE PGPORT

cd /workspace

echo "Waiting for postgres ${PGHOST}:${PGPORT}..."
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if pg_isready -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

echo "Applying base seed: tests/seed/client-compat-v1.sql"
psql -v ON_ERROR_STOP=1 -f tests/seed/client-compat-v1.sql

echo "Applying browser-compat YAML seed: tests/seed/browser-compat.yaml"
bash tests/seed/apply-yaml-seed.sh tests/seed/browser-compat.yaml

echo "Seed complete."
