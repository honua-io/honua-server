# pgRouting test/dev database

A standalone PostGIS + pgRouting database for routing development and the gated
`PgRoutingProvider` integration test (issue #1266).

## Why this exists

The default Honua PostGIS images (`postgis/postgis:*`) **do not bundle the
pgRouting extension**, so `CREATE EXTENSION pgrouting` fails on them (see
migration `src/Honua.Server/Migrations/043_CreatePgRoutingTopology.sql` and
[ADR-0050](../../docs/contributor/adr/0050-routing-engine-choice-and-naserver-compat.md)).

This compose uses the official `pgrouting/pgrouting` image (PostGIS + pgRouting
bundled) and seeds a tiny deterministic topology so a developer can solve routes
and service areas immediately without importing OSM data.

There are two ways to populate the routing topology:

1. **Default deterministic lattice seed** (current behavior, for tests / quick
   demo) — applied automatically on first container start. See below.
2. **Real OSM ingestion** via `import-osm.sh` / the `osm-import` compose
   profile — imports a real OpenStreetMap street network. See
   [Real OSM ingestion](#real-osm-ingestion). This **replaces** the lattice.

## Image

`pgrouting/pgrouting:17-3.5-3.7.3` — PostgreSQL 17, PostGIS 3.5, pgRouting 3.7.3.

## Start

```bash
docker compose -f docker/routing/compose.yml up -d
```

The database listens on `localhost:5434` by default (override with
`POSTGRES_ROUTING_PORT`). Credentials: database `honua_routing`, user `test`,
password `test`.

```bash
psql "host=localhost port=5434 dbname=honua_routing user=test password=test"
```

## Seed topology

`init-routing.sql` provisions the osm2pgrouting-compatible `ways` /
`ways_vertices_pgr` tables (matching migration 043 exactly) and seeds a 3x3
lattice of vertices at 0.01-degree spacing anchored at `(0,0)`:

```
7(0,.02) -- 8(.01,.02) -- 9(.02,.02)
   |            |             |
4(0,.01) -- 5(.01,.01) -- 6(.02,.01)
   |            |             |
1(0,0)   -- 2(.01,0)   -- 3(.02,0)
```

Every edge has `cost = reverse_cost = 1`. The least-cost path from vertex 1
(SW corner) to vertex 9 (NE corner) is 4 hops (cost 4, ~4.44 km geodesic).

> If you change this network, update the matching seed in
> `tests/dotnet/Honua.TestKit/PgRoutingFixture.cs` so the compose DB and the
> Testcontainer fixture stay identical.

## Point the integration test at this DB

The `PgRoutingProvider` integration test is gated behind `HONUA_ROUTING_TEST`
(so the heavy pgRouting image is never pulled on the default Fast tier). When
that env var is set, the test fixture honors `HONUA_ROUTING_TEST_DB_URL` and
uses this DB instead of starting its own Testcontainer:

```bash
export HONUA_ROUTING_TEST=1
export HONUA_ROUTING_TEST_DB_URL='Host=localhost;Port=5434;Database=honua_routing;Username=test;Password=test'
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --filter "Category=Routing"
```

Without `HONUA_ROUTING_TEST`, the test is skipped. With `HONUA_ROUTING_TEST=1`
but no `HONUA_ROUTING_TEST_DB_URL`, the fixture starts its own
`pgrouting/pgrouting` Testcontainer with the identical seed.

## Real OSM ingestion

The lattice seed is intentionally trivial. To route over a **real** street
network, import an OpenStreetMap extract with
[`osm2pgrouting`](https://github.com/pgRouting/osm2pgrouting). osm2pgrouting
builds its **own** osm2pgrouting-compatible `ways` / `ways_vertices_pgr`
schema (the same tables migration 043 defines), and the `--clean` flag it uses
**drops and recreates** them — so importing OSM **replaces the lattice seed**
in the target database.

A car-routing tag configuration (`mapconfig.xml`) and a tiny, fully synthetic
sample extract (`sample/sample.osm`) are bundled so the import path can be
smoke-tested offline.

### Open-data constraint (ADR-0050)

Only import **open** OSM data (e.g. [Geofabrik](https://download.geofabrik.de/)
or [openstreetmap.org](https://www.openstreetmap.org/) exports, ODbL). Do
**not** import Esri/Network Analyst or any other proprietary street data into
this database. The bundled `sample/sample.osm` is fully synthetic (hand-authored
coordinates, invented IDs) and is not derived from any proprietary source.

### Option A — `import-osm.sh` (host osm2pgrouting)

Prerequisite: `osm2pgrouting` installed on the host
(`sudo apt-get install osm2pgrouting`, or run it from a container — see
Option B). Start the routing DB first (`docker compose up -d`), then:

```bash
# Import the bundled synthetic sample (defaults match compose.yml).
docker/routing/import-osm.sh

# Import your own extract.
docker/routing/import-osm.sh --file /path/to/region.osm.pbf

# Override DB connection (flags or PG* env vars).
PGPORT=5434 docker/routing/import-osm.sh --file region.osm --conf docker/routing/mapconfig.xml
```

Flags: `--file`, `--conf`, `--host`, `--port`, `--dbname`, `--username`,
`--password` (`-h` for help). Connection defaults match this compose:
`localhost:5434`, db `honua_routing`, user/pass `test`/`test`. If
`osm2pgrouting` is not installed the script prints install guidance and exits
non-zero (it does not fail silently).

### Option B — `osm-import` compose profile (no host install)

A one-shot `osm-import` service (gated behind `profiles: ["osm-import"]`, image
[`iboates/osm2pgrouting`](https://hub.docker.com/r/iboates/osm2pgrouting), which
bundles the osm2pgrouting CLI — the base `pgrouting/pgrouting` image does not)
runs the import against the `pgrouting` service over the compose network. The
default `docker compose up -d` does **not** start it.

```bash
# Ensure the routing DB is up.
docker compose -f docker/routing/compose.yml up -d

# Import the bundled synthetic sample (replaces the lattice seed).
docker compose -f docker/routing/compose.yml --profile osm-import run --rm osm-import

# Import your own extract: drop it into docker/routing/sample/ and point
# OSM_FILE at the in-container path (the ./sample dir is mounted at /data/sample).
cp /path/to/region.osm docker/routing/sample/
OSM_FILE=/data/sample/region.osm \
  docker compose -f docker/routing/compose.yml --profile osm-import run --rm osm-import
```

### Verify the import

```bash
psql "host=localhost port=5434 dbname=honua_routing user=test password=test" \
  -c 'SELECT count(*) AS ways FROM ways;' \
  -c 'SELECT count(*) AS vertices FROM ways_vertices_pgr;'
```

To return to the deterministic lattice, recreate the volume:

```bash
docker compose -f docker/routing/compose.yml down -v
docker compose -f docker/routing/compose.yml up -d
```
