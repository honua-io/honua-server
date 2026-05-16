# Shared Seed Data (YAML)

This folder holds shared seed data used by both C# and Python integration tests.

## Format

The seed file is YAML with these top-level keys:

- `version`: schema version (current: `1`)
- `srid`: default SRID (optional)
- `profiles`: named collection subsets
- `collections`: table definitions
- `features`: rows to insert
- `sql`: optional SQL statements to run

### Collections

Each collection defines a table:

- `name`: logical collection name
- `table`: database table name (defaults to `name`)
- `geometry_column`: geometry column name (defaults to `geom`)
- `geometry_type`: `Point`, `LineString`, `Polygon`, etc. (defaults to `GEOMETRY`)
- `geography`: `true` to use PostGIS `geography` instead of `geometry`
- `srid`: overrides the top-level `srid`
- `id_column`: defaults to `id`
- `id_type`: SQL type for the id column (defaults to `SERIAL PRIMARY KEY`)
- `properties`: column name → SQL type

### Features

Each feature references a collection and may specify:

- `id`: id column value
- `geometry`: `wkt` or `geojson`
- `properties`: column values

### Profiles

Profiles let you seed a subset of collections:

```yaml
profiles:
  core:
    collections: [places]
```

## C# Usage

```csharp
using Honua.TestKit.Seeding;

var schema = await postgres.CreateSeededSchemaAsync(
    nameof(MyTests),
    "tests/seed/seed.yaml",
    profile: "core");
```

Or apply to an existing schema:

```csharp
await postgres.ApplySeedAsync("tests/seed/seed.yaml", schema, profile: "core");
```

## Python Usage

```python
from shared.seed import SeedRunner

runner = SeedRunner("tests/seed/seed.yaml")
runner.apply(postgis.get_connection(schema), schema=schema, profile="core")
```

## Additional Seed Files

| File | Purpose | Applied by |
|------|---------|------------|
| `tests/seed/base-schema.sql` | Shared base schema, tables, indexes, and deterministic `test_service` + layer 0 feature seed data; recorded as `tests/seed/base-schema.sql:test_service:layer0` in SDK compatibility evidence | All CI integration-test jobs (`js-integration-tests`, `esri-leaflet-browser-tests`, `mcp-certification`, `mcp-llm-smoke`, `sdk-server-compatibility`) |
| `tests/seed/client-compat-v1.sql` | Versioned client compatibility certification seed snapshot; canonical source for desktop/BI smoke evidence, Docker real-client interop GDAL/PyQGIS lanes, and the Python STAC client compatibility lane | `windows-client-compat-nightly.yml`, `client-interop-nightly.yml` (`gdal`, `pyqgis`), `tests/python/stac_client` |
| `tests/seed/mobile-offline-demo-v1.sql` | Deterministic SDK-backed mobile offline field-operations fixture with service/layer/form metadata, provisional offline manifest metadata, baseline edit records, and safe reset semantics | Manual local/staging/cloud provisioning for honua-server#895; see [Mobile Offline Demo Fixture](../developer/mobile-offline-demo-fixture.md) |
| `tests/seed/mobile-offline-demo-conflict-delta.sql` | Advances the mobile offline conflict target from `sync_version = 1` to `sync_version = 2` after package download | Manual/mobile harness conflict scenario for honua-server#895 |
| `tests/seed/admin-sample-feature-server.yaml` | Local admin UI sample FeatureServer fixture (`admin_sample`, layers `3000`-`3002`) with Oahu point, projected line, and polygon features, extents, fields, and renderer metadata | Manual/local PostGIS bootstrap after `base-schema.sql` or a migrated database |
| `tests/seed/mcp.yaml` | MCP certification data (second service, polygon layer, deterministic features) | CI `mcp-certification` and `mcp-llm-smoke` jobs |
| `tests/seed/browser-compat.yaml` | Browser compatibility service with point/line/polygon layers (IDs 2000–2002) and seeded features in the San Francisco area; anonymous access | CI `maplibre-compat` job (via `setup-honua-server` action) |
| `tests/seed/apply-yaml-seed.sh` | Extracts SQL from a YAML seed file with a top-level `sql:` key and applies via `psql` | CI `mcp-certification`, `mcp-llm-smoke`, and `maplibre-compat` jobs |

`mcp.yaml` uses `version: 1` with a top-level `sql:` key containing raw SQL statements. In CI, it is applied after `base-schema.sql` via `apply-yaml-seed.sh`. This is distinct from the collections format (used by `seed.yaml`) which also uses `version: 1` but with `collections`/`profiles`/`features` keys and is consumed by `SeedRunner`. Other SQL-array seeds like `server.yaml` and `odata.yaml` follow the same format but are loaded by the C# test harness via `SeedRunner`/`WebAppFixture.UseSeed()`, not by `apply-yaml-seed.sh`. See [MCP Certification](mcp-certification.md) for details.

`client-compat-v1.sql` is intentionally a versioned snapshot instead of an alias to the moving CI base seed. When the client compatibility workflow needs a different dataset, add a new snapshot (`client-compat-v2.sql`) rather than rewriting `v1`.

The current snapshot seeds anonymous access for service `test_service`, layer/collection `0`, and layer title `Test Layer` so the Windows client compatibility transcripts and manual follow-through remain repeatable. It also enables `postgis_raster` and creates the raster metadata tables expected by raster-aware startup paths in the Docker client-interop stack; the browser-specific service and layers are applied separately from `tests/seed/browser-compat.yaml`.

For a local admin UI sample service, reset the shared test schema and apply the admin sample fixture:

```bash
bash scripts/dev/seed-admin-sample.sh
```

The script uses the same connection defaults as the other seed helpers:
`PGHOST=localhost`, `PGPORT=5432`, `PGUSER=honua`, `PGPASSWORD=honua`,
and `PGDATABASE=honua_test`. Override those environment variables for a
different local database.

To run the steps manually:

```bash
psql -f tests/seed/base-schema.sql
bash tests/seed/apply-yaml-seed.sh tests/seed/admin-sample-feature-server.yaml
```

That creates `admin_sample` with point layer `3000`, projected line layer
`3001`, and polygon layer `3002`, so the UI can preview these routes without
fake client-side rows:

```text
/rest/services/admin_sample/FeatureServer/3000/query?f=geojson&where=1%3D1
/rest/services/admin_sample/FeatureServer/3001/query?f=geojson&where=1%3D1
/rest/services/admin_sample/FeatureServer/3002/query?f=geojson&where=1%3D1
```

The admin sample seed is deterministic and safe to re-run. It removes only the
`admin_sample` service/layer bindings and the reserved sample object ids before
reinserting known rows.

## Profiles in CI

Use a profile to keep seed data minimal for fast tests:

```bash
HONUA_TEST_DB_SEED_PATH=tests/seed/seed.yaml
HONUA_TEST_DB_SEED_PROFILE=core
```
