# ADR-0051: Branch-Versioning Storage Model (Overlay/Moment Over the Live Base Table)

## Status

Accepted — implemented and merged via #1504 (foundation), #1508 (overlay storage +
reads/writes), #1509 (reconcile/post), and #1510 (`VersionManagementServer` +
`gdbVersion` routing); #1272 closed. As-built divergences and deferred refinements are
noted inline and tracked in #1511.

## Context

Esri-style **branch versioning** (issue #1272, epic #1270) requires Honua to serve
and edit named versions that diverge from the published `DEFAULT` data, then
`reconcile`/`post` those edits back. The open architectural question is **how
versioned edits are physically stored and read**, and which **native PostgreSQL
features** to lean on. This decision is the substrate the deferred wiring in
PR #1500 will build on, and it must coexist with #371 (named versions /
reconcile-post UX), #1285 (temporal "as-of" history), and #1287 (durable
disconnected-sync conflict records).

Three facts about the existing implementation constrain the choice:

- **`DEFAULT` data is the live base `features` table — never copied.** A layer is
  a logical, `layer_id`-discriminated slice of one physical `features` table
  (`objectid`, `layer_id`, `geometry` with `GIST`, `attributes JSONB` with `GIN`).
  The read pipeline selects directly `FROM features WHERE layer_id = $1`
  (`FeatureQueryBuilder.Build.cs`). Any version model must **overlay onto this
  live table, not fork it.**
- **The change log is OID-only.** `honua.feature_changes`
  (`012_AddReplicationDurability.sql`) records *that* an `(layer_id, objectid)`
  changed at a monotonic `honua.sync_generation`, via the `track_feature_changes()`
  AFTER-ROW trigger — it does **not** store the row's geometry/attribute image.
  `PostgresChangeTracker.GetChangesSinceAsync` collapses this log into a net op
  (the reusable kernel for reconcile/post). #1285 temporal reads are a *projection
  over this same generation cursor*, not a separate store. Consequence: the change
  log can tell you *which* OIDs a version touched but **cannot reconstruct the
  version's row values** — so any model must answer *where branch row values live*.
- **Track B foundation already landed the storage dimension + contracts
  (PR #1500, inert for `DEFAULT`).** `047_CreateGdbVersions.sql` (the
  `honua.gdb_versions` registry with `common_ancestor_gen`/`branch_gen` pointers),
  `048_AddVersionToChangeLog.sql` (nullable `feature_changes.version_id` + a
  version-aware trigger driven by a transaction-scoped `honua.gdb_version` GUC —
  **GUC unset ⇒ NULL ⇒ byte-identical to pre-versioning**), and the canonical
  `GdbVersion`/`VersionContext`/`IVersionManager` contracts. This implicitly
  commits to an overlay/moment direction; this ADR confirms it and specifies the
  storage mechanics #1500 deferred.

A hard non-negotiable: the **`DEFAULT` read/edit path must remain byte-identical**
so the 952/952 OGC CITE baseline cannot regress when `gdbVersion` is absent.

### Approaches weighed

1. **Overlay / moment model.** `DEFAULT` stays the live base table; branch row
   *values* live in a shared, version-discriminated overlay table; a version read
   replays the overlay onto `DEFAULT` at a generation "moment."
2. **Per-version physical delta tables.** One `features_v<id>` table set per version
   (DDL per version), overlaid on `DEFAULT`.
3. **Bitemporal / system-versioned rows in the base table.** Valid-time/txn-time
   rows in `features`; a branch is a named view over row history.
4. **Hybrid.** Overlay by default; promote a hot/long-lived version to its own
   indexed delta table.
5. **Synthetic `layer_id` discriminator.** A version is a synthetic
   `branch_layer_id` (allocated from a high range); branch rows live in the shared
   `features` table under that id. DEFAULT queries (filtered by the base layer_id)
   never see them, and the existing change-tracking trigger covers branch rows with
   no new plumbing. (This is the approach an independent parallel implementation
   chose — weighed here because its reuse of the layer_id discriminator is genuinely
   elegant.)

| Axis | 1 Overlay | 2 Per-version tables | 3 Bitemporal base |
|---|---|---|---|
| `DEFAULT` byte-identical (CITE) | ✅ emits today's exact SQL | ✅ | ❌ adds temporal predicate to every `DEFAULT` read |
| Scales with | edits (small) | edits + per-table catalog churn | whole-table history (taxes primary workload) |
| Create/drop version | O(1) registry row | **DDL per version** (locks, catalog churn) | cheap pointer, but write-amplifies forever |
| Isolation | strong (physical overlay) | strong | weak (predicate-only; leak risk) |
| Storage | only edited rows | only edits + N× table/index overhead | full history in the primary table |
| Reuse of foundation | maximal (rides `feature_changes`, collapse kernel, conflict classifier, `IFeatureWriter`) | needs DDL/registry/dynamic SQL | rebuilds temporal + replication on a new substrate |
| Rollback | drop one table | migrate N tables | rewrites base-table contract |

Per-version DDL tables are strictly dominated by the overlay (same benefits, DDL
churn cost). Bitemporal is the "purest" history model and natively solves the
OID-only gap, but it moves version state into the `DEFAULT` hot path and **cannot
guarantee the byte-identical `DEFAULT` query** — disqualifying for #1272.

The **synthetic `layer_id` discriminator (Approach 5)** deserves a direct rebuttal
because it is simpler than the overlay and reuses more existing infrastructure — it
needs no `version_edits` table and no new trigger. It is rejected for one decisive
structural reason plus two semantic ones:

- **ObjectID identity (decisive).** `features.objectid` is the *global* primary key,
  so the same `objectid` cannot exist under both the base layer_id and a branch
  layer_id. A branch therefore cannot preserve a DEFAULT feature's OID — but Esri
  branch versioning, ArcGIS clients, and reconcile-by-OID all require OID stability
  across versions. No amount of layer_id machinery fixes this: it is a consequence
  of the global-PK schema. The overlay table keys on `(version_id, layer_id,
  objectid)`, so the *same* OID lives in DEFAULT and in the branch overlay — identity
  preserved. This is the single deciding factor.
- **No fork / empty branch.** With a registry-only create (no copy) and a read that
  swaps to `branch_layer_id` (no UNION with base), a freshly created branch is
  *empty* — you cannot edit an existing DEFAULT feature in isolation, only add rows
  to a scratch layer. The overlay read (`base ⊎ branch edits`) gives true fork
  semantics with copy-on-first-touch.
- **Reconcile/post.** Merging branch edits back to DEFAULT under a separate layer_id
  means re-keying OIDs on post (because of the PK collision above), which the model
  has no clean answer for; the overlay's shared-OID rows replay onto `features`
  directly.

The layer_id-discriminator insight (versions ride the existing change-tracking trigger
for free) is real, and the overlay model captures the same benefit by tagging
`feature_changes.version_id` from the same trigger — without inheriting the OID
collision.

## Decision

**Adopt the overlay / moment model (Approach 1)** for v1, adding the one piece
`047/048` left open: a shared, version-discriminated overlay table that stores
branch row *values*.

- **New migration `049_CreateVersionEdits.sql`** (additive, inert for `DEFAULT`):
  `honua.version_edits(version_id UUID REFERENCES gdb_versions, layer_id INT,
  objectid BIGINT, operation SMALLINT, geometry GEOMETRY, attributes JSONB,
  base_geometry GEOMETRY NULL, base_attributes JSONB NULL, branch_gen BIGINT,
  PRIMARY KEY (version_id, layer_id, objectid))` with a covering btree on
  `(version_id, layer_id, objectid)`, a `GIST(geometry)`, and a partial index
  `WHERE operation = <delete>`.
- **Version read** (emitted only when `!VersionContext.IsDefault`):
  `(DEFAULT base minus OIDs the branch shadows) UNION ALL (overlay non-deletes)`,
  parameterized on `version_id` and bounded by `CommonAncestorGeneration`. When
  `IsDefault`, the builder emits **nothing extra** — today's exact SQL — which is
  the CITE firewall.
- **Version write** routes the INSERT/UPDATE/DELETE to `version_edits` inside the
  existing edit transaction, with `SET LOCAL honua.gdb_version = '<uuid>'` so the
  trigger tags `feature_changes.version_id`. `DEFAULT` edits set no GUC and target
  `features` — byte-identical.

### Resolved sub-decisions (recommendations adopted)

1. **Capture the ancestor row image on first branch-touch.** When a version first
   shadows an `(layer_id, objectid)`, snapshot the then-current `DEFAULT` row into
   `version_edits.base_geometry`/`base_attributes`. This populates #1287's
   `BaseStateJson` and lets reconcile classify conflicts as
   *base vs current-`DEFAULT` vs current-branch* rather than the weaker
   *current-vs-current*. Cost is one extra read on first touch only.
2. **Single global generation sequence for v1; posts serialize on generation
   advance.** Accept this throughput model for the first release rather than
   introducing per-layer generation streams. Per-layer streams are a future
   optimization if/when post throughput becomes a bottleneck (a shared design
   point with #371).
3. **Ship v1 overlay-only; defer hybrid promotion (Approach 4).** No per-version
   indexed-table promotion until the overlay anti-join cost is *measured* on large
   layers. If promotion is later warranted, prefer `LIST`-partitioning
   `version_edits` by `version_id` over per-version DDL tables.

### Native PostgreSQL features

**Use:** the transaction-scoped GUC + trigger (`048`); JSONB row images in the
overlay (column-compatible with `features`); targeted indexes **on the overlay
only**; the durable `sync_generation` sequence as the "moment."

**Avoid (with reasons):**

- **MVCC snapshots** (`pg_export_snapshot` / `SET TRANSACTION SNAPSHOT`) as
  "moments" — session-scoped, short-lived, and pin vacuum; a branch lives for
  weeks. The durable generation number is the moment.
- **Base-table partitioning by `version_id`** — routes every `DEFAULT` query
  through partition pruning, taxing the byte-identical guarantee. (Partitioning the
  *overlay* is an optional later scaling lever only.)
- **Row-Level Security** for version visibility — forces a policy predicate onto
  every `DEFAULT` read and, with pooling + GUCs, a leaked GUC becomes a
  wrong-visibility security incident. Keep version routing in explicit SQL.
- **Rules / `INSTEAD OF` views** — rule-system footguns / dynamic DDL; use inline
  SQL mirroring `FeatureQueryBuilder.Temporal.cs`.
- **System-versioned temporal extensions** (`temporal_tables`, `periods`,
  PG18 native temporal), **logical replication / publications, FDW, materialized
  views** — wrong tools for this storage problem (and per-version DDL would *break*
  publications).

## Consequences

- The `DEFAULT` read/edit path is provably unchanged, protecting CITE 952/952.
- Version reads scale with *edits*, not table size, so long-lived/divergent
  branches do not tax the primary `DEFAULT` workload.
- Branch versioning is honestly **Postgres-only** and localized to one overlay
  table plus one query/writer branch; DuckDB/SQL Server/MySQL register a
  `NotSupported` `IVersionManager` (`SupportsVersioning = false`).
- Reconcile/post reuse the existing collapse kernel, generation sequence,
  `ReplicaConflictRecord` taxonomy (#1287), and `IFeatureWriter` — no parallel
  conflict model or history store.
- Rollback is non-destructive: drop `version_edits` and the deferred query/writer
  branches; `feature_changes.version_id` may remain (NULL-only). No `DEFAULT` data
  is ever touched.
- Storing the ancestor image on first touch adds bounded write/storage cost per
  branch-edited row; acceptable for correct reconcile.

## Implementation surface (as built)

- `PostgresVersionManager` implementing `IVersionManager` over `gdb_versions` +
  `version_edits`; `CreateAsync` stamps `common_ancestor_gen = currval(sync_generation)`.
- `FeatureQueryBuilder.Version.cs` (mirror `FeatureQueryBuilder.Temporal.cs`):
  no-op when `IsDefault`, else the overlay `UNION` form. Thread a nullable
  `VersionContext` onto `FeatureQuery`.
- Writes (`FeatureDataAccess.Edits.cs` / `IFeatureWriter` / `FeatureEditBatch`):
  set `SET LOCAL honua.gdb_version` and route to `version_edits`; capture ancestor
  on first touch.
- Reconcile/post via `Honua.Jobs` under a Redis-backed version lock; replace the
  hard `gdbVersion is not supported` rejections
  (`FeatureServerRequestHandlers.Edits.cs:431/542/741`) with
  `IVersionManager.ResolveAsync`.
- `VersionManagementServer` GeoServices protocol slice
  (create/delete/alter/start*/stop*/reconcile/post/versions/versionInfo),
  Enterprise-gated, advertised in FeatureServer metadata.
- `049_CreateVersionEdits.sql` is the only additive schema change beyond `047/048`.

### As-built divergences and open risks (tracked in #1511)

- **Post does not use `IFeatureWriter` literally.** This ADR specified post replays
  via the shared `IFeatureWriter`; as built, `PostAsync` uses direct parameterized
  SQL on `features` (in one transaction, through the same change-tracking trigger)
  because `IFeatureWriter`'s `Feature` model auto-assigns objectids and round-trips
  attributes through a typed dictionary — which would drop branch-created OIDs and
  JSONB fidelity. A future transaction-scoped / explicit-OID `IFeatureWriter` overload
  would let post use the shared writer as intended.
- **Overlay read at scale is unvalidated.** The version read
  (`base WHERE objectid NOT IN (overlay OIDs) UNION ALL overlay`) under spatial
  predicates has not been measured on a large, highly-divergent version; the GIST
  plan across the UNION/anti-join is the risk. Mitigation is hybrid promotion
  (Approach 4) or LIST-partitioning `version_edits` by `version_id`, deferred until
  measured. The DEFAULT path is unaffected.
- **Single global generation sequence serializes posts.** Accepted for v1; per-layer
  generation streams are a future option if concurrent multi-version post throughput
  becomes a bottleneck.
- **Esri `startReading`/`stopReading`/`startEditing`/`stopEditing`** are stateless
  acknowledgements (the overlay/moment model carries the version per-request via
  `gdbVersion`, so there is no server-held session); session-token semantics can be
  added if a target client requires them.

## Cross-references

- #1272 — Branch-versioned editing + production offline replica/change-tracking
  (closed; this ADR decided the branch-versioning storage model, implemented across
  #1504/#1508/#1509/#1510).
- #1511 — Branch-versioning follow-ups (post via `IFeatureWriter`, overlay-read-at-scale
  validation, session-stateful edit, per-layer generation streams).
- #1270 — Epic: editing-model parity.
- #371 — Named versions, reconcile/post, multi-user concurrent editing (owns the
  reconcile/post UX + auto-resolution policy this substrate serves).
- #1285 — Temporal data history API (shares the `feature_changes` generation cursor;
  post should write a temporal checkpoint).
- #1287 — Durable disconnected-sync conflict records (reconcile reuses
  `ReplicaConflictRecord`; `BaseStateJson` is fed by the first-touch ancestor image).
- ADR-0046 — Progressive `IDatabaseSession` migration (transaction model used by edits).
