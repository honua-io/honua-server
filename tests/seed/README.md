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

## Profiles in CI

Use a profile to keep seed data minimal for fast tests:

```bash
HONUA_TEST_DB_SEED_PATH=tests/seed/seed.yaml
HONUA_TEST_DB_SEED_PROFILE=core
```
