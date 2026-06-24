# PROJ grid-data PostGIS image (datum fidelity)

A standalone PostGIS image that bakes the canonical PROJ **NADCON / NADCON5 /
NTv2 / GEOID** grid files into the runtime PROJ data directory, so grid-gated
Esri-default datum transformations resolve at full accuracy (issue #1501,
non-functional-parity epic #1273).

## Why this exists

Honua reprojects geometry with PostGIS' embedded PROJ engine and routes every
output-CRS `ST_Transform` through the shared `DatumTransformSql` chokepoint
(`src/Honua.Postgres/Features/FeatureStore/Services/DatumTransformSql.cs`) plus
the import overload (`migration 053_AddImportDatumTransformation.sql`).

The **base** `postgis/postgis:*` images ship only the minimal Debian
`libproj-data` subset — the legacy NTv1 `nad27`/`nad83` ASCII tables and a handful
of `.gsb` grids. The canonical modern `.tif` grids that the EPSG/PROJ
coordinate-operation pipelines reference (e.g. `us_noaa_conus.tif` for NAD27↔NAD83
CONUS, the `us_noaa_nadcon5_*` realization chain, `ca_nrc_ntv2_0.tif`,
`us_noaa_g2018u0.tif` for GEOID18) are **absent**. As a result, grid-gated
pipelines either fall back to a lower-accuracy bundled operation or, for pipelines
PROJ cannot satisfy from the bundled data, fail the explicit-grid contract.

This image provisions exactly those grids (see [`grids.txt`](grids.txt)) into
`/usr/share/proj`, so PROJ resolves the high-accuracy grid operation offline
(`PROJ_NETWORK=OFF` — no CDN access at runtime).

## What is provisioned

[`grids.txt`](grids.txt) is the auditable manifest. It is kept in sync with the
`requiredGrids` entries in
`src/Honua.Core/Features/Infrastructure/Crs/Resources/esri-default-datum-transformations.json`
and the grid names in `docs/internal/evidence/datum-transformation-parity.md`.
The grids are fetched from the PROJ CDN (`https://cdn.proj.org`) at **build time**
by [`fetch-grids.sh`](fetch-grids.sh); the build fails loudly if any grid is
missing or truncated.

## Isolation

This image is **opt-in**. Nothing in the default build / Fast-tier test / CITE
path pulls or builds it, and the base PostGIS tag used everywhere else
(`tests/dotnet/Honua.TestKit/PostgresFixture.cs`, the CITE compositions) is
unchanged. Wiring this image (or its baked grids) into the integration/CITE
compositions — which would bump those image tags — is a deliberate follow-up, not
part of the default path.

## Build / run

```bash
docker compose -f docker/proj-grids/compose.yml up -d --build
psql "host=localhost port=5436 dbname=honua_test user=test password=test"
```

The database listens on `localhost:5436` by default (override with
`POSTGRES_PROJ_GRIDS_PORT`). Credentials: database `honua_test`, user/pass
`test`/`test`.

To pin a different base PostGIS tag:

```bash
docker build -f docker/proj-grids/Dockerfile \
  --build-arg BASE_IMAGE=postgis/postgis:18-3.6 \
  -t honua-postgis-proj-grids:local docker/proj-grids
```

## Point the gated integration test at this DB

The grid-fidelity test (`DatumGridProvisioningTests`) is gated behind
`HONUA_PROJ_GRID_TEST` so the heavy grid image is never built/pulled on the
default Fast tier. When that env var is set, the test honors `HONUA_TEST_DB_URL`
and runs against this DB:

```bash
export HONUA_PROJ_GRID_TEST=1
export HONUA_TEST_DB_URL='Host=localhost;Port=5436;Database=honua_test;Username=test;Password=test'
dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
    --filter "FullyQualifiedName~DatumGridProvisioning"
```

Without `HONUA_PROJ_GRID_TEST`, the test is skipped. The standard parity tests
(`DatumTransformationParityTests`) continue to run against the default
grid-less fixture and assert the explicit-failure / default-path contract.

## Verify the grids are present

```bash
docker compose -f docker/proj-grids/compose.yml exec proj-grids-db \
  sh -c 'ls -la /usr/share/proj/*.tif'

# A CONUS NAD27 -> NAD83 point should land on the canonical NADCON grid shift:
docker compose -f docker/proj-grids/compose.yml exec proj-grids-db \
  psql -U test -d honua_test -c \
  "SELECT ST_AsText(ST_Transform(ST_SetSRID(ST_MakePoint(-100,40),4267),4269));"
```
