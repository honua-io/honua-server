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

These are recorded as out of scope for the first slice so the routing capability
ships against a bounded, reviewable contract.

### Versioned topology lifecycle foundation

Issue #2715 adds the storage and domain contract needed to implement network-content
editing safely without making editing itself available. Each dataset has immutable,
monotonically numbered topology generations in one of `draft`, `dirty`, `building`,
`ready`, `active`, `failed`, or `retired`. The lifecycle permits only a `ready`
generation to become `active`; an active generation can only become `retired`, so a
content writer can never mutate the live solve target. State mutations use a
compare-and-swap row version and failures expose a stable sanitized code rather than
provider details.

Migration 084 backfills exactly one active generation per existing dataset while
leaving `honua.network_datasets` as the resolver source of truth. That makes the
foundation additive and safe for rolling upgrades: old and new binaries continue to
solve against the same edge/vertex mapping. New registry entries create their initial
active generation through a database-owned insert trigger, so even a pre-084 replica
in a mixed-version rollout cannot commit a registration with zero generations. New
application code validates that invariant before committing its registration
transaction. The migration installs this trigger before taking its backfill snapshot,
closing the concurrent-registration window during startup. While the legacy registry
remains authoritative, mapping updates from an old or rolled-back replica atomically
retire the recorded active generation and create its replacement; lifecycle metadata
therefore cannot silently diverge from the topology that solves actually use.
Delivery remains deliberately ordered under #2656:
transactional content edits (#2716), isolated durable rebuild (#2718), atomic
promotion/rollback (#2719), then multi-node fencing and recovery (#2720). Travel-profile
metadata and cost semantics remain independently owned by #2655.

### Transactional edge and turn-restriction content edits (#2716)

Issue #2716 adds the canonical admin edit service the lifecycle foundation was built for.
Migration 086 adds three additive tables scoped to one `(dataset_id, generation)` pair:
`network_topology_edge_edits` (staged geometry + allowlisted attributes),
`network_topology_restriction_edits` (staged turn restrictions, foreign-keyed to the staged
edges so a dangling reference is a database-enforced rejection), and
`network_topology_edit_idempotency` (an at-most-once ledger that stores only counts and
lifecycle state, never geometry or attribute values). Two new admin endpoints under
`/api/v1/admin/network-datasets/{id}/generations` allocate a `draft` generation (seeded from
the dataset's current active generation) and list a dataset's generations; a third,
`POST .../generations/{generation}/edits`, applies a batched add/update/delete edit for
edges and turn restrictions in one Postgres transaction.

Every batch requires an `Idempotency-Key` header (replaying the same key with the same
payload returns the original result without re-applying; the same key with a different
payload is rejected deterministically) and an `If-Match` header carrying the generation's
current row version (a mismatch is a 409, not a silent overwrite). `NetworkTopologyLifecycle`
(the same provider-neutral module #2715 introduced) gained `TryApplyContentEdit`: it only
ever leaves a generation `dirty`, accepts edits when the current state is `draft` or `dirty`,
and rejects every other state — active, ready, building, failed, and retired generations all
return a sanitized 409 rather than accepting content. A successful batch increments both the
row version (concurrency) and the source revision (content clock) in the same
compare-and-swap `UPDATE` that applies the state transition, so the dirty transition and the
content mutation commit atomically. Validation runs in two layers: `NetworkTopologyEditValidation`
checks structure in memory (bounded batch size, duplicate ids, GeoJSON `LineString`/
`MultiLineString` shape and SRID match, turn-restriction kind/penalty shape, and the edge
attribute keys against the dataset's #2655 travel-profile forward/reverse cost columns)
before a transaction opens, while id-collision and turn-restriction edge-reference checks run
transactionally against the staged content (an `ON CONFLICT DO NOTHING` plus affected-row
check for create/update/delete semantics, and an explicit existence pre-check for restriction
references) so `Honua.Routing` never needs a direct Npgsql dependency to detect them. The
NAServer/GeoServices protocol adapter never calls this edit service and remains read-only.

### Durable shadow-topology rebuild, atomic promotion/rollback, and multi-node fencing (#2718/#2719/#2720)

The remaining three children of umbrella #2656 land together. Migration 087 adds
`network_topology_rebuild_attempts` (one row per rebuild attempt, carrying a monotonic
fencing token, lease owner/expiry/heartbeat, and terminal evidence) and
`network_topology_rebuild_checkpoints` (per-stage snapshot/build/analyze/validate/cleanup
progress). Migration 088 adds `network_topology_promotions`, an immutable
promote/rollback history table.

**Rebuild (#2718).** `POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild`
creates a rebuild attempt — atomically transitioning the generation `dirty` → `building` via
the same `NetworkTopologyLifecycle.TryTransition` compare-and-swap #2715 introduced — then
submits a `NetworkTopologyRebuild` execution job through the *existing* shared durable job
infrastructure (`IExecutionJobStore`/`IJobQueue`/`IJobExecutor`, `src/Honua.Core/Features/
ControlPlane`, `src/Honua.Jobs/Features/ControlPlane`): no parallel job system was built.
The worker-side `NetworkTopologyRebuildJobExecutor` runs entirely in-process against
Postgres — no remote batch-compute backend — and calls
`NetworkTopologyShadowTopologyBuilder` to materialize an isolated, generation-scoped
pgRouting-shaped edge/vertex shadow topology directly from the generation's staged content
edits (`honua.network_topology_edge_edits`/`..._restriction_edits`, #2716). This
deliberately does not use `pgr_createTopology`'s geometry-tolerance snapping: #2716 edits
already carry explicit, validated stable vertex references, which is a more precise source
of truth than re-inferring connectivity from geometry, and it means a rebuild has no
dependency on the optional `pgrouting` extension (migration 043 already treats it as
optional). Graph-integrity evidence is a portable SQL-only check (edge/vertex/self-loop
counts) rather than `pgr_analyzeGraph`, for the same portability reason. Every stage is
checkpointed so a restarted worker resumes cleanly rather than restarting from scratch.
Completion atomically transitions the attempt and the owning generation to `ready` (or
`failed`), stamping the generation's own `edge_table`/`vertex_table` columns with the
shadow tables — the single source of truth promotion later copies into
`honua.network_datasets`.

**Multi-node fencing (#2720).** Every rebuild-attempt mutation (lease acquire/takeover,
heartbeat, checkpoint write, completion, failure) is gated by a monotonic fencing token
scoped to the attempt row, verified inside one atomic SQL statement rather than a
separate check-then-write round trip. This is intentionally a narrower, attempt-scoped
mechanism layered on top of — not a replacement for — the shared job infrastructure's own
claim/heartbeat/retry machinery (`ExecutionJobRecord.ClaimedBy`/`LastHeartbeatAt` plus
optimistic-concurrency `Version` CAS, which the research for this delivery confirmed is
the existing fencing-equivalent mechanism generic across every `ExecutionJobKind`); the
topology-specific token exists because the mutation paths it fences (checkpoints, shadow
artifacts, the generation's own lifecycle) are store methods this feature owns, not the
generic job record. `NetworkTopologyRebuildReconciler` (run periodically by a thin
`BackgroundService`) finds attempts whose lease has expired: if the owning execution job is
already terminally failed, the attempt is failed with a stable sanitized code and its
orphan shadow tables are dropped; otherwise the job is re-enqueued so a fresh worker claims
it and takes over the lease. Deferred: true multi-node chaos/scale tests (simulated Redis
outage mid-rebuild, rolling-deployment overlap, remote Kubernetes/AWS Batch backends) and a
dedicated telemetry/metrics dashboard for self-healing status — structured logs and the
rebuild-attempt status endpoint cover the same information today, just not as a
purpose-built dashboard.

**Atomic promotion/rollback (#2719).** `POST /api/v1/admin/network-datasets/{id}/promote`
and `.../rollback` share one transactional helper: lock the active and target generation
rows, verify state/evidence/artifact preconditions (a `ready` candidate's shadow tables
still exist for promotion; a `retired` target's tables still exist, i.e. are not
retention-cleaned, for rollback), retire the old active generation and activate the target
via the same lifecycle compare-and-swap, then repoint `honua.network_datasets` to the
target generation's own `edge_table`/`vertex_table`/`srid` columns — which the rebuild
completion path already stamped — so `NetworkDatasetRegistry`/`INetworkDatasetResolver`
picks up the new snapshot on its very next read with no resolver code change. `Rollback`
required one small, explicit extension to `NetworkTopologyLifecycle.CanTransition`:
`Retired -> Active`, documented as the rollback-only transition distinct from the normal
`Ready -> Active` promotion path. Repointing the registry runs with the legacy
`network_datasets_track_legacy_mapping_update` trigger (migration 084) disabled for the
duration of the transaction — that trigger exists to keep the old admin registry-PUT path
safe by auto-retiring-and-forking a brand new generation, which would conflict with this
transaction's explicit retire-and-activate-an-existing-generation sequence; disabling it is
scoped to the transaction (transactional DDL) and never affects the legacy path outside it.
Every promotion is `Idempotency-Key`-scoped (replaying the same key returns the original
history entry rather than re-promoting) and recorded in the immutable
`network_topology_promotions` table.

**Update (post-MVP, #1862 / #1863).** Two deferrals from the original first slice
are now delivered:

- **Barriers (point/line/polygon)** are implemented. The NAServer adapter parses
  `barriers` / `polylineBarriers` / `polygonBarriers` Esri FeatureSets into a
  protocol-neutral `RouteBarrier` (kind + GeoJSON) and threads them into both
  Route and ServiceArea solves. The pgRouting provider honours them by excluding
  the graph edges each barrier restricts (point → nearest edge via the GiST `<->`
  KNN; line/polygon → every `ST_Intersects` edge), recomputing the edges-SQL for
  `pgr_dijkstra` / `pgr_drivingDistance`. A provider that does not advertise a
  barrier kind (e.g. the straight-line mock) returns a GeoServices 400 rather than
  silently dropping the barrier. Bounded by `Routing:MaxBarriers` (default 1000).
- **Multiple travel modes** are dataset-backed: the request surface
  (`travelMode`, bare token or Esri object `name`), validation against the
  provider's advertised `SupportedTravelModes`, and capability advertisement are
  wired. Migration 083 stores validated profile-to-forward/reverse-column mappings
  on each network dataset; only mappings whose columns actually exist are
  advertised. All pgRouting solve families use the selected pair. The built-in
  topology intentionally remains **driving-only** until operators supply genuine
  non-driving weights. Unsupported modes return a GeoServices 400 — Honua does not
  fabricate a mode-specific solve it cannot honour.

**Update (post-MVP, #1861 / #1864 / #1874 / #1882 / #2652).** The remaining
operation-level deferrals are now delivered: ClosestFacility uses the canonical
provider contract and real pgRouting impedance, OD cost matrix returns bounded
cost-only cells, LocationAllocation supports minimize-impedance and
maximize-coverage, and the addressable network-dataset registry has
admin-authorized mapping/metadata CRUD. Route solve accepts both GET query
parameters and POST form parameters through the same handler. The remaining
subfeature deferrals are OD line geometry, additional allocation objectives,
and edge/vertex/turn-restriction editing with topology rebuild; these are tracked
by #2653-#2656.

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
