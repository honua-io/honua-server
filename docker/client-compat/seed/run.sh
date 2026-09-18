#!/usr/bin/env bash
# Applies the canonical client-compat seed bundle to a freshly-started
# postgres so honua + lane services see the expected services and layers:
#   - tests/seed/client-compat-v1.sql → schema + `test_service` (layer 0)
#     used by both the pyqgis and gdal lanes (the gdal lane points at the
#     same pair via HONUA_GDAL_SERVICE_ID / HONUA_GDAL_COLLECTION_ID)
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

# Coverage fixture: the base seed declares a service advertising ImageServer,
# Wcs and OGC-API-Coverages bound to layer 0, but seeds no raster there, so
# every coverage surface resolved a service and then found nothing. Applied
# right after the base seed because it references honua.layers(layer_id) = 0.
echo "Applying raster coverage seed: tests/seed/client-compat-raster-v1.sql"
psql -v ON_ERROR_STOP=1 -f tests/seed/client-compat-raster-v1.sql

echo "Applying browser-compat YAML seed: tests/seed/browser-compat.yaml"
bash tests/seed/apply-yaml-seed.sh tests/seed/browser-compat.yaml

echo "Applying auth breadth YAML seed: tests/seed/client-compat-auth-wave1.yaml"
bash tests/seed/apply-yaml-seed.sh tests/seed/client-compat-auth-wave1.yaml

# Portal/Sharing facade fixture (epic #1240 / #1372): public/org/private-tier
# services the arcgis-stub Portal lane and the licensed ArcGIS Pro / Field Maps
# evidence runs discover through /sharing/rest. Applied after the base SQL seed
# (which owns honua.seed_metadata_v2_compat_snapshot) so the Metadata v2 graph
# the Portal item projector reads includes the tiered services.
echo "Applying portal-compat YAML seed: tests/seed/portal-compat.yaml"
bash tests/seed/apply-yaml-seed.sh tests/seed/portal-compat.yaml

# PMTiles archive for the pmtiles/archive-read certification cell. Published
# through the running server: LocalFileStorage indexes its objects once at
# construction, so an archive written to disk afterwards is invisible to the tile
# proxy.
#
# Explicitly non-fatal. Every SQL seed above has already applied by this point,
# and every lane gates on this container exiting 0, so aborting here would take
# out the whole matrix for one optional artifact. A failure is not hidden either:
# the pmtiles cell's own test fails on the 404 rather than skipping.
echo "Publishing PMTiles archive for test_service/0"
if python3 /usr/local/bin/publish-pmtiles.py; then
    :
else
    echo "WARNING: PMTiles publish did not complete; the pmtiles certification cell will fail on a 404." >&2
fi

echo "Generating the 3D Tiles / I3S scene from browser_compat/2002"
if python3 /usr/local/bin/publish-scene.py; then
    :
else
    echo "WARNING: scene generation did not complete; the 3d-tiles and i3s certification cells will fail on a 404." >&2
fi

echo "Seed complete."
