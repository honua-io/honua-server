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
| `tests/seed/base-schema.sql` | Shared base schema, tables, indexes, and deterministic `test_service` + layer 0 feature seed data | All CI integration-test jobs (`js-integration-tests`, `mcp-certification`, `mcp-llm-smoke`) |
| `tests/seed/mcp.yaml` | MCP certification data (second service, polygon layer, deterministic features) | CI `mcp-certification` and `mcp-llm-smoke` jobs |
| `tests/seed/apply-yaml-seed.sh` | Extracts SQL from a YAML seed file with a top-level `sql:` key and applies via `psql` | CI `mcp-certification` and `mcp-llm-smoke` jobs |

`mcp.yaml` uses `version: 1` with a top-level `sql:` key containing raw SQL statements. In CI, it is applied after `base-schema.sql` via `apply-yaml-seed.sh`. This is distinct from the collections format (used by `seed.yaml`) which also uses `version: 1` but with `collections`/`profiles`/`features` keys and is consumed by `SeedRunner`. Other SQL-array seeds like `server.yaml` and `odata.yaml` follow the same format but are loaded by the C# test harness via `SeedRunner`/`WebAppFixture.UseSeed()`, not by `apply-yaml-seed.sh`. See [MCP Certification](mcp-certification.md) for details.

## Profiles in CI

Use a profile to keep seed data minimal for fast tests:

```bash
HONUA_TEST_DB_SEED_PATH=tests/seed/seed.yaml
HONUA_TEST_DB_SEED_PROFILE=core
```
