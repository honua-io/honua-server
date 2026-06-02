# ADR-0040: Metadata v2 Canonical Graph Design

## Status
Accepted (cutover in progress on `feat/metadata-v2-cutover`, PR #1157)

## Context

The v1 metadata surface (`ILayerCatalog`, `LayerDefinition`, `ServiceDefinition`,
`LayerMetadata`, `ServiceMetadata`, `CatalogMetadata`) emerged organically while
honua-server grew from a single FeatureServer prototype into a multi-protocol
service supporting:

- **APIs**: OGC API Features/Maps/Tiles/Coverages/Records/Styles/Common, WMS 1.3,
  WMTS 1.0, WFS 2.0, WCS 2.0, Esri FeatureServer/MapServer/ImageServer, STAC API
  1.0, OData v4
- **Package formats**: GeoJSON, GeoParquet, FlatGeobuf, GeoPackage, KML, GML,
  Shapefile, MBTiles, PMTiles, COG, Zarr, NetCDF, Esri JSON

Five structural problems with v1 became blocking by mid-2026:

1. **Conflated identities.** `LayerDefinition.Id: int` doubled as the storage row
   id, the GeoServices `/rest/services/{n}/0` path segment, AND the unique-within-
   catalog key. Different protocols needed different identifiers, and v1 couldn't
   express them independently.
2. **Mixed structural / display / capability / settings on one type.**
   `LayerDefinition` carried geometry definition + min/max scale + queryable flag +
   editor-tracking fields + permanent filter expression all in one. Adding a new
   field touched every consumer.
3. **No projection contract.** Each protocol cluster (FeatureServer, OGC API
   Features, STAC, OData, …) built ad-hoc transformations from v1 types into
   wire format. The same Esri-style "drawingInfo" derivation appeared in three
   places. No single semantic mapping for shared concepts like "license".
4. **No structural validation.** Orphan FKs (publication pointing at missing
   resource, storage binding for deleted layer) survived in production catalogs.
   v1 had no integrity check.
5. **Storage coupled to admin tooling.** v1 was structured around the admin UI's
   editing model rather than around what the protocol surfaces actually needed.
   STAC item ingestion required round-tripping through admin-shaped types it had
   no use for.

The cutover replaces v1 with a canonical graph designed to project losslessly
into every supported API/format and validate structurally at construction time.

## Decision

### Principle 1: Three-axis decomposition

V2 separates three orthogonal concerns into distinct entity types:

| Axis | Entity | Concerns |
|---|---|---|
| **Data** | `MetadataV2Resource` | Schema, spatial, temporal, relationships, access policy, permanent filter, display hints, editing capabilities. Independent of protocol AND storage. |
| **Storage** | `MetadataV2StorageBinding` (+ `MetadataV2Connection`) | Physical materialization: connection, locator, storage layer id, capabilities. Independent of protocol. Many bindings per resource (primary + replicas). |
| **Protocol** | `MetadataV2Service` + `MetadataV2Publication` | How the data is exposed: protocols list, route paths, per-publication identifier, primary publication selection. Independent of storage. |

The same resource can be published through multiple services (FeatureServer +
OGC API Features + STAC) without duplication.

### Principle 2: Layered semantic vocabulary

Metadata facts live in one of three layers based on whether they're shared
semantic, format-specific, or pure derivation:

```
┌─────────────────────────────────────────────────────────────────┐
│ Layer 3 — Render-time projection (Map*ResponseV2 builders)      │
│ Pure derivation. Never stored. Examples: capability CSV strings,│
│ "supports*" flag derivation, server runtime constants.          │
├─────────────────────────────────────────────────────────────────┤
│ Layer 2 — Format-specific (Extensions["<format>:<key>"])        │
│ Concepts that exist in one format's vocabulary. Stable          │
│ namespaced keys. Examples: esri-templates, stac:eo, wms-render. │
├─────────────────────────────────────────────────────────────────┤
│ Layer 1 — Shared semantic (typed V2 graph slots)                │
│ Concepts that map to ≥2 standards with consistent meaning, OR   │
│ are properties of the data rather than of the wire format.      │
│ Examples: Identity, Spatial, Temporal, License, Schema, Style.  │
└─────────────────────────────────────────────────────────────────┘
```

#### Promote L2 → L1 criteria

Promote a field from Extensions to a typed slot when **any** of:
1. The concept maps to ≥2 major standards with consistent semantics.
2. The concept is consumed by ≥2 cluster handlers at render time.
3. The concept is a property of the **data** (license, geometry type) rather
   than a property of the **wire format** (XML namespace, JSON convention).

#### Stays L2 when

1. Named after one protocol (`Esri*`, `stac:*`, `wms:*`).
2. Encodes one format's response *shape* rather than the underlying concept.
3. Only one cluster's projection code consumes it.

#### Extensions namespace convention

Documented in `MetadataV2Resource.Extensions` / `MetadataV2Service.Extensions` /
`MetadataV2StorageBinding.Extensions` doc comments:

| Key prefix | Owner |
|---|---|
| `stac`, `stac:<ext>` | STAC ecosystem (eo, sar, view, proj, raster, processing, mlm, web-map-links, …) |
| `esri-<aspect>` | Esri runtime fields (esri-popup, esri-templates, esri-subtypes, esri-sync, esri-render, esri-time, esri-document-info, esri-paging, esri-runtime) |
| `wms-render` | WMS render flags (opaque, fixedWidth, fixedHeight, noSubsets) |
| `wcs` | WCS XML encoding hints |
| `odata-annotations` | OData annotations that aren't derivable |
| `indexes` | Informational DB index metadata |
| `geotiff-tags` | GeoTIFF tags beyond core (StorageBinding only) |
| `fgdc`, `iso19139` | Sidecar metadata standards |

Adding a new prefix → propose a typed slot first; only use Extensions when
the field fails the promote criteria.

### Principle 3: Typed slots over open bags

Every concept that meets the L1 criteria gets a typed C# record or property —
not a `JsonElement` blob, not a `Dictionary<string, string>`. Examples:

- `Spatial` → `MetadataV2ResourceSpatial` record with typed `SpatialReference`,
  `GeometryType` (enum), `Bbox`, `PrimaryGeometryField`
- `Temporal` → `MetadataV2ResourceTemporal` with field names + extent
- `PermanentFilter` → `MetadataV2PermanentFilter` with expression + language
- `Field.Type` → `MetadataV2FieldType` enum (replaced string-tagged provider labels)

Typed slots earn compile-time safety and explicit documentation. Open bags
(`Extensions`, `Options`) earn extensibility for genuinely format-specific cases.

### Principle 4: One source of truth per fact

When multiple v1 concepts encoded the same fact through different mechanisms,
V2 collapses to one:

| Multiple v1 mechanisms | One V2 source of truth |
|---|---|
| `Service.ServiceType` (enum) + `Service.EnabledProtocols` (list) + `Publication.Protocol` (string) | `Service.Protocols: IReadOnlyList<string>` |
| `Publication.LayerIndex` + `ServiceLocalId` + `Path` | `Publication.Identifier { Value, IsNumeric, PathOverride }` |
| `Resource.StorageBindingIds` + `PrimaryStorageBindingId` | `Resource.StorageBindingIds[0]` is primary by convention |
| `Field.Type` (string) + per-cluster type-string parsers | `MetadataV2FieldType` enum (single mapping table) |

Derived properties carry the legacy access pattern (e.g. `Publication.LayerIndex`
is a `[JsonIgnore]` computed property reading `Identifier.Value` when `IsNumeric`).

### Principle 5: Graph integrity validation at construction

`MetadataV2GraphValidator.Validate(graph)` enforces invariants:

- Unique ids per entity kind, FK targets exist
- `Resource.StorageBindingIds` reference declared bindings; bindings claim back
- `Publication.{ResourceId, ServiceId}` reference declared entities
- `Resource.Spatial.PrimaryGeometryField` exists in `SchemaFields` with Geometry/
  Geography type
- `Resource.Temporal.{StartTimeField, EndTimeField}` exist as Date/DateTime/Time
  schema fields
- Schema field names unique within a resource
- At most one `geometry.primary` / `id.primary` semantic-role field per resource
- At most one `IsPrimary=true` publication per `(resourceId, serviceId)`

Stores call the validator before persisting; in-memory graph builders call it
in tests. Errors are stable strings so consumers stay reliable across releases.

### Principle 6: No v1→v2 adapter shim

Pre-release status enables a hard cutover. We accept porting cost in exchange
for not carrying compatibility code into production. Tactical exceptions during
the in-flight cutover (the documented "SQL last-mile bridge") are time-boxed
to the cutover branch and removed when the corresponding V2 infrastructure
lands.

## Rejected alternatives

### Alternative A: Adapter layer between v1 and v2
Build `LayerDefinition.FromV2Resource(...)` and `ServiceDefinition.FromV2Service(...)`
to let v1 consumers keep working unchanged while v2 producers exist alongside.

**Rejected because**: doubles the surface area indefinitely. v1's structural
problems (Principles 1–4) would persist in the adapter outputs, propagating
through every cluster handler.

### Alternative B: Per-protocol metadata classes
`EsriLayerDefinition`, `StacCollection`, `OgcApiFeatureCollection`, etc. — each
cluster gets its own typed shape.

**Rejected because**: shared concepts (license, attribution, spatial extent,
temporal extent) duplicate across N classes. The same dataset published through
3 protocols would need 3 inconsistent metadata records. This was effectively
v1's situation.

### Alternative C: One giant typed type
`MetadataV2Resource` carries every field every format could ever want, all typed.

**Rejected because**: chases a moving target (STAC extensions, OGC API draft
specs, vendor-specific Esri fields evolve faster than canonical type can be
updated). Extensions bag with documented vocabulary is the right escape hatch.

### Alternative D: Inline storage of every format's payload
`Resource.Esri = <EsriJson>`, `Resource.Stac = <StacItem>`, etc. — store the
literal projection alongside the canonical form.

**Rejected because**: storage explodes, sync drift between canonical and
projections becomes inevitable, and the projection logic still has to live
somewhere. Layer 3 derivation at render time is the right call.

### Alternative E: Catalog as a first-class entity
Add `MetadataV2Catalog` with parent/child hierarchy for STAC catalogs / Esri
folders / OGC Records hierarchies.

**Rejected (deleted in slice 63/N)** because: catalog *projection* is an output
concern, not a graph-storage concern. Endpoints walk publications filtered by
`PublicationType` and project to each catalog's wire format. Esri folder
grouping handled by convention (dotted `Metadata.Name` or future `Service.Group`
slot). STAC parent/child can be modeled via `Relationships` with role `parent`
or `child` when a concrete consumer needs it.

## Consequences

### Positive
- **Single source of truth.** Every shared concept lives in exactly one typed
  slot. Adding a new protocol consumer reads from the same slots existing ones
  use; no parallel "Esri metadata" / "STAC metadata" / "OGC metadata" structs.
- **Validated at construction.** Orphan FKs, missing primary geometry fields,
  duplicate semantic roles surface immediately rather than during request
  handling.
- **Typed safety in hot paths.** `MetadataV2FieldType` enum replaces ~6
  string-table parsers across the codebase. Compiler catches typos.
- **Extensible without re-architecting.** STAC extensions, vendor-specific
  Esri fields, format-specific render flags all land in namespaced
  `Extensions` keys with documented conventions.
- **Render-time projection composable.** `Map*ResponseV2` builders in each
  cluster consume the same typed slots and emit format-specific wire shapes.
  Adding a new format means writing one new projection module, not touching
  upstream metadata.
- **Storage-format independence.** Resources can move between Postgres,
  GeoParquet, FlatGeobuf, MBTiles without metadata changes — only the
  `StorageBinding` changes.

### Negative
- **Large cutover.** ~110+ commits across the cutover branch. Every protocol
  cluster handler needs porting (FeatureServer, OData, OGC API Features, WMS,
  WMTS, WFS, WCS, MapServer, ImageServer, STAC, OGC API Maps/Tiles/Coverages/
  Records). Adapter contracts (`IQueryParameterAdapter`, `IEditParameterAdapter`)
  carry v1 + V2 method overloads during the transition.
- **Crosswalk maintenance.** New OGC API specs, STAC extensions, or Esri runtime
  fields all need triage against the Layer 1/2/3 criteria. The crosswalk doc
  (`docs/contributor/architecture/metadata-v2-crosswalk.md`) is a living
  artifact.
- **Extensions vocabulary requires discipline.** The namespaced keys convention
  works only if it's enforced in code review. A "stuff it in Extensions" reflex
  would defeat the purpose. The Extensions doc comments + this ADR are the
  enforcement record.

### Trade-offs we accept
- **Some format-specific consumers still need stable Extensions keys.** Layer 2
  is not a transitional layer — it's a permanent home for genuinely
  format-specific facts. We name them well and document them.
- **Per-resource integer storage handle (`StorageBinding.StorageLayerId`)
  remains.** Storage backends (Postgres / MySql / DuckDB / SqlServer / Oracle
  FeatureStores, ILayerStyleCatalog, OutputCacheInvalidationService) take an
  int. That int is the storage abstraction boundary, not a v1 leak.
- **Adapter contract methods carry v1 + V2 overloads** during the cutover.
  Default-interface V2 overloads with `NotSupportedException` defaults let
  incremental porting happen. The v1 method is deleted after the last cluster
  ports.

## Implementation references

- **Code**: `src/Honua.Core/Features/Metadata/Domain/V2/`
  - `MetadataV2Graph.cs` — top-level entity types
  - `MetadataV2Enums.cs` — typed enums (FieldType, GeometryType, ResourceType, …)
  - `MetadataV2Spatial.cs`, `MetadataV2Temporal.cs`, `MetadataV2PermanentFilter.cs` — typed sub-records
  - `MetadataV2GraphIndex.cs` — built lookups (ResourcesById, ResourcesByStorageLayerId, etc.)
  - `MetadataV2GraphValidation.cs` — invariant enforcement
- **Crosswalk**: `docs/contributor/architecture/metadata-v2-crosswalk.md`
- **Cutover plan**: `docs/contributor/architecture/metadata-v2-cutover-plan.md`
- **Cutover PR**: #1157

## Related ADRs

- **ADR-0002** (MapLibre as Canonical Style Format) — styles in V2 use the same
  multi-encoding container, with MapLibre as one of N encodings on a `Type=Style`
  resource.
- **ADR-0009** (Shared Filter AST) — V2 `IFilterExpressionService` accepts both
  v1 `LayerDefinition` and V2 `MetadataV2Resource` overloads of `Translate` /
  `ParseAndNormalize` / `Normalize`.
- **ADR-0033** (Unified License Format) — V2's `Metadata.License` field is the
  canonical home for the unified license identifier.
- **ADR-0035** (Provider-Ready Data Source Binding) — V2's
  `MetadataV2StorageBinding` is the realization of this concept.

## Open design questions tracked separately

These are gaps the crosswalk identified that need typed slots before more
handler porting:

1. `Metadata.Links[]` — required by every OGC API spec
2. `Metadata.{License, Attribution, Keywords, Themes, Language, ContactPoint, Publisher}` — universal catalog facets
3. `Field.{Alias, Editable, Length, DefaultValue, Domain}` — Esri/OData parity
4. `Resource.Display.*` — every Map/Tile protocol needs MinScale/MaxScale/etc.
5. `Resource.Editing.*` — Esri capability + OGC API Features Part 4 parity
6. `Service.Settings.*` — operational limits per protocol
7. `Resource.Spatial.{SupportedCrs, StorageCrs, StorageCrsCoordinateEpoch}` — OGC API Features Part 2
8. `Resource.Style` + `Resource.StyleResourceIds` — OGC API Styles + multi-encoding.
   **Resolved by ADR-0048.** The typed slots exist but are dormant scaffolding: nothing
   produces a `Type=Style` resource and `StyleResourceIds` is never populated. ADR-0048
   sets the first-class style model + OGC API – Styles contract, and tracks the
   producer + styleId-keyed storage as a phased data-layer epic.

The 5-slice landing plan is in `metadata-v2-crosswalk.md`.

## Decision date
2026-05-23 (formalizing decisions taken across cutover slices 31/N – 113/N)
