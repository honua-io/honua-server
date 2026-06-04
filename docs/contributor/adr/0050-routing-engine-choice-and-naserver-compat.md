# ADR-0050: Routing engine choice and NAServer compatibility

## Status

Accepted (MVP scope — pgRouting; legal sign-off on the GPL-over-SQL posture
tracked by honua-io/honua-esri-assess#33).

## Context

`honua-io/honua-server#1265` / `#1266` introduce a Network Analyst routing
capability for the GeoServices REST surface: an Esri-compatible `NAServer`
exposing route (point-to-point) and service-area (drive-time / drive-distance
polygon) solves. Esri clients reach this surface through the
`NAServer/Route/solve` and `NAServer/ServiceArea/solveServiceArea` operations,
and Honua adapts them into a shared routing pipeline behind an
`IRoutingProvider` abstraction, exactly as the other GeoServices services adapt
into canonical query/edit/process pipelines.

The decision in front of us is which routing engine backs that pipeline for the
MVP. The candidates are not equivalent — they differ in licensing posture, the
infrastructure they add to the deployment, whether they natively produce
service-area (isochrone) polygons, and how the .NET app reaches them. Three
constraints dominate:

1. **Infrastructure budget.** The default `honua-server` deployment is
   `honua-server` + PostgreSQL/PostGIS + Redis. Adding a routing engine should
   not, for the MVP, force a new sidecar container, a JVM, or a heavy
   OSM-tile build into the baseline stack (cf. the lean-image constraint that
   shaped ADR-0038).
2. **Service areas are half the requirement.** `#1266` asks for both route and
   service-area solves. An engine that does point-to-point routing well but has
   no native isochrone surface only satisfies half the capability.
3. **Data-licensing compliance.** The routing network must come from open data
   (OpenStreetMap) or the customer's own network data. Honua must not lift Esri
   network datasets or Esri-licensed street data. The Esri NAServer *API shape*
   is reimplemented as a compatibility surface — APIs are fair use per
   *Google v. Oracle* — but the network *data* behind it is open or
   customer-owned.

## Decision

**Honua uses pgRouting as the MVP routing engine** for the NAServer route and
service-area solves.

- **Route solves** map to `pgr_dijkstra` over the topology network table.
- **Service-area solves** map to `pgr_drivingDistance` to compute the reachable
  edge/node set, then an alpha-shape / concave-hull polygonization of the
  reachable vertices to produce the Esri `saPolygons` output.
- **`travelDirection`** (`esriNATravelDirectionFromFacility` /
  `esriNATravelDirectionToFacility`) is honored: `FromFacility` runs
  driving-distance over the outbound graph (outbound coverage); `ToFacility` runs
  it over the reversed graph (source/target swapped) so it computes who can reach
  the facility within the cost cutoff.

### Rationale

- **Lowest infrastructure.** pgRouting is a PostgreSQL extension. It runs
  inside the PostGIS instance that is already in the compose stack, so the MVP
  routing capability adds **no new container, sidecar, or runtime** to the
  baseline deployment.
- **Pure SQL over the existing data path.** The .NET app reaches pgRouting the
  same way it reaches PostGIS today — SQL over Npgsql through the existing
  Postgres provider path. There is no new transport, no new client library, and
  no new network hop.
- **Geometry stays in-DB.** Route geometry and service-area polygons are
  produced in PostgreSQL, so the Esri JSON shaping happens in the existing
  Postgres adapter path alongside the other GeoServices responses. No round-trip
  to an external engine and back is needed to assemble the response.

### Licensing posture (handle deliberately)

pgRouting is licensed **GPL-2.0-or-later**. Honua's use of it is **only over the
SQL / network boundary**: the .NET application links no GPL code — it sends SQL
to PostgreSQL exactly as it does for PostGIS, and PostgreSQL loads the extension
in its own process. Under the common interpretation of the GPL, invoking a
GPL program over a database/network boundary (the .NET app and pgRouting are
separate programs communicating over SQL) does **not** make the closed-source
Honua server a derivative work of pgRouting.

There is one redistribution caveat to honor:

- **If Honua ever ships or redistributes a container image that bundles
  pgRouting**, that distribution conveys a GPL component, and the corresponding
  GPL **source offer** must be made available for that component. The
  application's proprietary code remains separate, but the bundled pgRouting
  binaries carry their GPL obligations into any image we publish.

Legal sign-off on this GPL-over-SQL posture (and the bundled-image source-offer
obligation) is tracked by **honua-io/honua-esri-assess#33**
(`epic-esri-ip-compliance-guardrails`). The data-licensing constraint from
`#1266` is independent and absolute: routing networks come from **open data
(OSM)** or the **customer's own network data**; Honua does not lift Esri network
datasets or Esri-licensed street data. The NAServer API shape itself is a
compatibility reimplementation and is fair use per *Google v. Oracle*.

### Alternatives considered

| Engine | License | New infra | Native service-area / isochrone | .NET call path | Verdict |
|---|---|---|---|---|---|
| **pgRouting** | GPL-2.0-or-later | None (extension in existing PostGIS) | Via `pgr_drivingDistance` + alpha-shape/concave-hull (coarser) | SQL over Npgsql, existing Postgres path | **Chosen** — lowest infra, in-DB geometry, SQL-only boundary |
| Valhalla | MIT | +1 sidecar container; heavier OSM tile build | Yes — best-in-class native isochrone, maps cleanly to Esri `saPolygons` | HTTP to sidecar | **Documented fallback** — switch here if legal rejects the GPL-over-SQL posture or if production isochrone quality demands it |
| OSRM | BSD | +1 sidecar container | **No native isochrone** | HTTP to sidecar | Rejected — service areas are half the requirement, and OSRM cannot produce them natively |
| GraphHopper | Apache-2.0 | +1 sidecar container; adds a JVM | Yes — native isochrone | HTTP to sidecar (JVM) | Rejected — adds a JVM with no advantage over Valhalla as the non-GPL fallback |

Valhalla is recorded as the **clean fallback** precisely because the
`IRoutingProvider` abstraction keeps the engine swappable: if legal rejects the
GPL-over-SQL posture, or if production isochrone fidelity outgrows the
alpha-shape approach, Honua can move route and service-area solves to a Valhalla
provider without changing the NAServer adapter contract.

## Consequences

### Positive

- **No new infrastructure for the MVP.** Routing rides on the PostGIS instance
  already in the stack. Operators who never deploy a second image gain the
  NAServer capability for free.
- **Single data path.** Routing reuses the existing Npgsql/Postgres adapter,
  error mapping, telemetry, and Esri JSON shaping. There is no parallel client,
  transport, or response assembler.
- **Engine is swappable.** The `IRoutingProvider` abstraction means Valhalla
  (or another provider) is a clean replacement behind a stable NAServer
  contract.

### Negative / costs

- **Coarser service-area polygons.** Alpha-shape / concave-hull isochrones are
  geometrically coarser than purpose-built drive-time contouring. This is a
  **documented cosmetic limitation, not a bug** — it is the accepted tradeoff
  for keeping routing in-DB with no sidecar. Valhalla is the documented upgrade
  path if production isochrone quality demands it.
- **Extension provisioning (resolved).** The `pgrouting` extension is not in the
  stock `postgis/postgis` images, so the dev `docker-compose.yml` and the Aspire
  AppHost now use `pgrouting/pgrouting:17-3.5-3.7.3`. The topology migration
  (`043_CreatePgRoutingTopology.sql`) is **guarded**: it provisions the extension
  and `ways` / `ways_vertices_pgr` tables only when `pgrouting` appears in
  `pg_available_extensions`, otherwise it raises a notice and no-ops. This keeps
  routing opt-in and lets every other environment (CITE, scale-test, the CI
  matrix, and nightly/e2e workflows that still run on stock PostGIS) boot
  unchanged — they are intentionally **not** swapped to the heavier Debian image.
- **Dev-image TLS caveat.** The Debian-based `pgrouting/pgrouting` image does not
  honor the `POSTGRESQL_CONF_*` knobs the alpine image used, so the dev DB no
  longer forces `ssl=on` / connection logging. Acceptable for local dev; enforce
  via a mounted `postgresql.conf` or `command:` override if a hardened dev DB is
  needed.
- **Network ingestion (implemented).** The dedicated `docker/routing/` stack
  seeds a deterministic lattice by default and provides a real OSM ingestion path
  via `docker/routing/import-osm.sh` (`osm2pgrouting`) plus an `osm-import`
  compose profile, with a bundled synthetic sample network. Customer-owned
  networks load through the same `ways` / `ways_vertices_pgr` topology contract.
- **GPL redistribution obligation.** Any published image bundling pgRouting must
  convey the GPL source offer for that component (see Licensing posture above).

### MVP scope and deferrals

The MVP delivers **route (`Route/solve`)** and **service-area
(`ServiceArea/solveServiceArea`)** solves, including **`travelDirection`**
(From/ToFacility) on service areas — `ToFacility` solves over the reversed graph.
Explicitly deferred:

- Origin-destination cost matrix (`NAServer/...solveODCostMatrix`)
- Location-allocation
- Closest facility (`ClosestFacility/solveClosestFacility`)
- Network-dataset editing
- Multiple travel modes
- Barriers (point/line/polygon)

These are recorded as out of scope for the first slice so the routing capability
ships against a bounded, reviewable contract.

**Input bounds (DoS guard).** The NAServer adapter caps input counts to bound
serial DB fan-out (each stop is a Dijkstra leg; each facility×break is a
driving-distance query). The caps are configurable via the `Routing` section
(`Routing:MaxStops`, `Routing:MaxFacilities`, `Routing:MaxBreaks`; defaults
1000 / 1000 / 50) and over-cap requests return a GeoServices `400` envelope.

## References

- `honua-io/honua-server#1265` / `#1266` — Network Analyst routing capability
  (NAServer route + service-area solves; open-data / GPL-compliance constraints).
- `honua-io/honua-esri-assess#33` — `epic-esri-ip-compliance-guardrails`
  (tracks legal sign-off on the GPL-over-SQL posture and the bundled-image
  source-offer obligation).
- [Esri Network Analyst Service (NAServer)](https://developers.arcgis.com/rest/services-reference/enterprise/network-analyst-service/) —
  route sync service and ServiceArea solve response shapes (`saPolygons`).
- [Esri Route synchronous service](https://developers.arcgis.com/rest/services-reference/enterprise/route-synchronous-service/).
- [pgRouting `pgr_dijkstra`](https://docs.pgrouting.org/latest/en/pgr_dijkstra.html) —
  route solves.
- [pgRouting `pgr_drivingDistance`](https://docs.pgrouting.org/latest/en/pgr_drivingDistance.html) —
  service-area reachability for alpha-shape / concave-hull polygonization.
- *Google LLC v. Oracle America, Inc.*, 593 U.S. ___ (2021) — API
  reimplementation as fair use.
- ADR-0029 — Geoprocess Canonical Model Mappings (canonical-pipeline adapter
  pattern this NAServer surface follows).
- ADR-0038 — GeoETL Pipeline Architecture and Runtime Boundary (lean-image /
  no-new-infrastructure precedent for the MVP).

## Decision date

2026-06-03
