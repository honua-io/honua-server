# I3S / ArcGIS Scene Layer Compatibility Matrix and Conformance Plan

This page is a research-spike output for `honua-server-843`. It maps Esri
Indexed 3D Scene Layer (I3S) / Scene Service concepts to Honua's planned
3D Tiles scene model, identifies the minimum viable compatibility surface,
and proposes bounded child tickets for implementation.

**No production I3S serving is added by this spike.** All routing patterns,
contracts, and recommendations below describe future work that must be
sequenced behind sibling tickets `honua-server-842` (3D Tiles generation
pipeline) and `honua-server-849` (protected scene access envelope), and
must remain Enterprise-edition only.

## Status vocabulary

- **Translation adapter**: a Honua source already exists (or will exist after
  `honua-server-842`) and the I3S surface can be served as a thin re-projection
  of that source. Implementation cost is bounded; correctness risk is in the
  format conversion.
- **Native pipeline**: no equivalent exists in the 3D Tiles plan. I3S would
  require its own indexing/encoding pipeline. Implementation cost is high and
  cannot piggyback on `honua-server-842`.
- **Deferred / Enterprise-only**: not a candidate for the initial Enterprise
  offering. Tracked here for completeness; revisit when an Enterprise customer
  asks for it.

## Spec target

The conformance plan targets **I3S 1.7** (OGC Community Standard `17-014r8`
plus Esri errata) as the primary compatibility version. Rationale:

- I3S 1.7 is the version most broadly supported across ArcGIS Pro 2.x/3.x,
  ArcGIS Enterprise 10.9+, and the ArcGIS Online Scene Viewer.
- I3S 2.0 (compact node bundles, restructured REST API) is OGC-ratified but
  client adoption is uneven; client compatibility is documented but not the
  initial implementation target.

I3S 2.0 SHOULD be tracked as a follow-on capability and any v1 endpoint
shape SHOULD avoid 1.7-specific framing in shared abstractions
(`ISceneLayerService`, see "Prerequisite abstractions" below).

## Layer-type compatibility

| Feature area | I3S concept | Honua equivalent (post-`honua-server-842`) | Translation feasibility | Recommendation |
| --- | --- | --- | --- | --- |
| Service info | `SceneServer` root JSON | Layer metadata + capabilities discovery | Easy — pure JSON projection | Translation adapter |
| Layer descriptor | `layers/0` JSON (`store`, `nodePages`, `materialDefinitions`, `geometryDefinitions`, `textureSetDefinitions`, `attributeStorageInfo`) | 3D Tiles `tileset.json` + layer metadata | Easy — JSON projection plus structural mapping | Translation adapter |
| `3DObject` layer type | Per-feature meshes with attributes | B3DM tiles (from `honua-server-842`) | Moderate — geometry and attribute re-encoding | Translation adapter (after `honua-server-842`) |
| `IntegratedMesh` layer type | Photogrammetry mesh tiles, single texture per node | B3DM mesh tiles | Hard — fixed-depth HLOD vs. flexible tree mismatch, single-texture constraint | Native pipeline; Enterprise-deferred |
| `Point` layer type | Discrete feature points with attributes | B3DM (point geometry) or future PNTS | Moderate | Translation adapter (after `honua-server-842`) |
| `PointCloud` layer type | Compressed dense point bundles, per-point attributes | PNTS tiles (from `honua-server-842`) | Moderate — bundle format conversion required | Translation adapter (after `honua-server-842`) |
| `Building` (BIM composite) | Composite scene of sub-layers (`overview`, `full`, disciplines) with shared attribute storage | CMPT composite tiles | Hard — composite scene model and discipline filtering have no current Honua analog | Deferred / Enterprise |
| `Voxel` layer type | Volumetric voxel grids | Not modeled by 3D Tiles plan | Hard — no source data | Deferred / Enterprise |

## Metadata, attributes, and structural compatibility

| Feature area | I3S concept | Honua equivalent | Translation feasibility | Recommendation |
| --- | --- | --- | --- | --- |
| Node tree (HLOD) | Fixed-depth HLOD tree declared at layer level (`nodePages`) | 3D Tiles flexible implicit/explicit tree | Moderate — HLOD depth normalization required | Translation adapter (after `honua-server-842`) |
| Node descriptor | Per-node JSON: `obb`/`mbs`, `lodThreshold`, `children`, `geometryData`, `textureData`, `sharedResource` references | 3D Tiles tile JSON node | Easy — straight projection | Translation adapter |
| Bounding volumes | MBS (lon, lat, height-meters, radius-meters) and OBB in geographic coords | OBB / sphere in CRS84 / ECEF | Easy — standard coordinate math | Translation adapter |
| LOD selection | Screen-space distance formula (`maxScreenThreshold` / `maxScreenThresholdSQ`) | 3D Tiles `geometricError` (screen-space-error driven) | Moderate — different formulas, both screen-space-driven | Normalizable in translation adapter |
| Attribute storage info | Layer-level field declarations (name, type, encoding) | Per-feature property schema in tileset | Easy — schema projection | Translation adapter |
| Per-node attribute files | Separate binary attribute files per field per node (`attributes/f_N/0`) for identify/query | Per-feature properties in tile (no separate binary store) | Hard — no 3D Tiles analog; required for `identify` parity | Native pipeline; Enterprise-deferred |
| Statistics summary | `statistics/f_N/0` per-field summary | Honua attribute statistics live in catalog metadata | Moderate — projection over catalog stats | Translation adapter |
| Field domains | Coded-value and range domains per field | Honua catalog domains (FeatureServer parity) | Easy — JSON projection over existing domain model | Translation adapter |

## Geometry and material compatibility

| Feature area | I3S concept | Honua equivalent | Translation feasibility | Recommendation |
| --- | --- | --- | --- | --- |
| Geometry encoding | Esri-defined binary `geometries/0` (interleaved buffer with positions, normals, UVs, colors, feature ids, region ids) | glTF binary buffers in B3DM (from `honua-server-842`) | Hard — binary layout transcoding required | Native pipeline OR online transcoder; sequencing TBD per child issue |
| Geometry compression | Optional Draco (1.7) or Esri compact bundles (2.0) | glTF buffers (Draco optional in `honua-server-842`) | Hard — compression format and bundling differ | Native pipeline OR online transcoder |
| Materials | `materialDefinitions` entries with PBR-aligned properties (baseColor, metallic, roughness, alpha mode, double-sided) plus texture references | glTF 2.0 PBR materials | Easy — close 1:1 mapping with PBR-MetallicRoughness | Translation adapter |
| Texture sets | `textureSetDefinitions` referencing JPEG/PNG/KTX2/Basis bundles per node | glTF embedded textures (JPEG/PNG/KTX2) | Moderate — format conversion may be needed; KTX2/Basis preferred for parity with both ecosystems | Translation adapter |
| Per-feature ids | Required `feature-id` attribute, used to link geometry to attribute files and for `identify` | Per-feature `_BATCHID` in B3DM batch table | Moderate — id mapping required during transcoding | Translation adapter (sequenced with attribute pipeline) |

## Service endpoint compatibility

I3S serving follows a fixed-shape REST surface, with all sub-resources rooted
under a single `SceneServer`. Honua's planned mount convention is the same
GeoServices-style root path used by `FeatureServer`, `MapServer`, and
`ImageServer`; no new mount convention is introduced.

| I3S endpoint (1.7) | Honua route (planned, NOT IMPLEMENTED) | Translation feasibility | Recommendation |
| --- | --- | --- | --- |
| `GET /rest/services/{id}/SceneServer` | `GET /rest/services/{id}/SceneServer` | Easy — projection over service catalog | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0` | `GET /rest/services/{id}/SceneServer/layers/0` | Easy — projection over layer metadata + tileset model | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0/nodepages/{n}` | Same path | Easy — paged projection of tileset tree | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0/nodes/{nodeId}` | Same path | Moderate — node descriptor projection requires HLOD normalization | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0/nodes/{nodeId}/shared` | Same path | Moderate — shared resource projection (materials, textures) | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0/nodes/{nodeId}/geometries/0` | Same path | Hard — binary geometry transcoding from glTF to I3S buffer layout | Native pipeline OR online transcoder |
| `GET /rest/services/{id}/SceneServer/layers/0/nodes/{nodeId}/textures/0` | Same path | Easy — passthrough from glTF embedded texture | Translation adapter |
| `GET /rest/services/{id}/SceneServer/layers/0/nodes/{nodeId}/attributes/f_{n}/0` | Same path | Hard — no source for binary per-field attribute files; native generation required | Native pipeline; Enterprise-deferred |
| `GET /rest/services/{id}/SceneServer/layers/0/statistics/f_{n}/0` | Same path | Moderate — projection over Honua attribute statistics | Translation adapter |
| Auth / access gating | Layer-level + request-level | `honua-server-849` protected scene envelope | Dependency — `honua-server-849` is a hard prerequisite | Blocked on `honua-server-849` |

## Coordinate reference and geodesy

I3S 1.7 uses **WGS 84** (EPSG:4326) as its base spatial reference, with
**ECEF** (EPSG:4978) for geometry buffers, and **ENU local frames** anchored
on each node's MBS center. The Honua 3D Tiles plan (`honua-server-842`) uses
the same global frame (CRS84 + ECEF) for tile transforms, so the global
positioning translation is a no-op in normal cases.

CRS handling rules for any future translation adapter:

- Validate that the source layer is registered with a CRS that has a
  WGS84 / ECEF transform path. Reject layers whose source CRS cannot be
  transformed (return `422 Unprocessable Entity` with a structured problem
  response, mirroring Terrain).
- Vertical datum and unit assumptions follow the Terrain pattern: source
  band/feature elevations are assumed to be meters when no vertical unit
  is declared, and `verticalUnit` / `verticalDatum` are reported nullable
  in service metadata when unknown.
- Bounding volume conversion (Honua OBB → I3S MBS) must compute the smallest
  enclosing sphere on the OBB corners. MBS values are reported in `[lon, lat,
  height_m, radius_m]`; lon/lat are degrees, radius is meters in ECEF.

## LOD and screen-space-error normalization

I3S `lodSelection.maxScreenThreshold` (and the squared variant) measure tile
selection in screen pixels at a fixed device pixel ratio. 3D Tiles
`geometricError` is metric, applied via the client's screen-space-error
formula. The translation adapter must derive an `lodSelection` value from a
tile's `geometricError`, MBS radius, and the camera/canvas assumptions baked
into ArcGIS Scene Viewer.

Recommended approach: pre-compute the I3S-equivalent threshold at translation
time using the standard formula `screenThresholdPx ≈ geometricError *
referenceCanvasHeightPx / (2 * tan(referenceFovRad / 2) * mbsRadiusMeters)`,
with reference canvas height and FOV taken from ArcGIS Scene Viewer defaults
(committed as named constants in the adapter, with a documented override
path).

This produces valid but potentially suboptimal LOD transitions in clients
that use different canvas/FOV assumptions; it is the same tradeoff the
3D Tiles ecosystem already makes when normalizing across clients.

## Reference fixtures and compatibility targets

### Public / simple reference dataset (committed test fixtures)

Two source candidates are recommended; both are small enough to commit
alongside the eventual conformance harness.

1. **`Esri/i3s-spec` GitHub repository sample data**
   (<https://github.com/Esri/i3s-spec>, MIT License). The repository ships
   minimal fixtures in its `format/test_data/` and `docs/i3s/` example
   directories, including a small `3DObject` scene and an `IntegratedMesh`
   slice. Suitable for vendoring as committed test assets; license requires
   attribution in the repository's third-party notices.

2. **Synthetic minimal fixture (Honua-authored)**. A 5-node `3DObject` scene
   with schematically-valid I3S 1.7 JSON and stub geometry buffers, generated
   from a deterministic seed. Suitable for fast CI assertions that validate
   protocol shape without depending on a third-party source. This fixture
   would live under `tests/fixtures/scene/i3s/` once the conformance harness
   ticket is opened.

### Real-world manual smoke target

Esri's publicly hosted **Philadelphia Buildings** I3S Scene Layer (ArcGIS
Online, Creative Commons license per ArcGIS Online metadata) is the
canonical real-world interop target for manual smoke testing in ArcGIS
Scene Viewer. It exercises the `3DObject` layer type, multi-LOD nodes, and
attribute identify. Manual smoke is appropriate because automated ArcGIS
Scene Viewer harnessing is out of scope for the initial Enterprise offering
and requires a non-public Esri toolchain.

### Recommended starter fixture

Lead with the **synthetic minimal fixture** for CI gating because it has
no external dependency and is fully deterministic. Use the `Esri/i3s-spec`
samples as a secondary "real format" check in the same harness ticket, and
keep the Philadelphia Buildings layer as the manual ArcGIS Scene Viewer
smoke target only.

## Enterprise gating, licensing, and support boundary

I3S support is **Enterprise edition only** end-to-end. Open-core builds MUST
NOT advertise I3S as a supported protocol. Specifically:

- **Capabilities / discovery**: I3S routes return `403 Feature requires
  Enterprise edition` rather than `404` when the feature is not licensed.
  This mirrors the existing edition gate used by PrintingTools layout
  templates and the spatial analytics extensions, and lets clients detect
  the protocol boundary distinctly from "no such service".
- **Service catalog**: open-core service catalog responses MUST NOT include
  `SceneServer` entries even when a layer otherwise qualifies. Enterprise
  catalog responses include them when the layer's `EnabledProtocols` list
  authorizes I3S.
- **Documentation**: this matrix and any child-issue documentation MUST be
  cross-referenced from the Enterprise documentation surface and MUST NOT
  appear in the open-core MVP support list except as "Not implemented —
  Enterprise roadmap".
- **Support policy**: the initial Enterprise offering targets I3S 1.7 against
  ArcGIS Pro 2.x/3.x and ArcGIS Online Scene Viewer. ArcGIS Scene Viewer
  certification is **out of scope** for the initial offering and is called
  out as a separate roadmap item.
- **Third-party licensing**: any vendored `Esri/i3s-spec` test fixtures must
  preserve the upstream MIT attribution. The Philadelphia Buildings smoke
  target is referenced by URL only, not vendored.

## Prerequisite abstractions

This spike surfaces a missing core abstraction: `Honua.Core` has no
`ISceneLayerService` (or equivalent scene-layer-source interface). Without
it, both `honua-server-837` (hosted 3D Tiles serving) and a future I3S
adapter risk duplicating scene indexing, caching, and telemetry logic
inline in their respective protocol features.

The interface SHOULD be defined as part of `honua-server-837` (or as a
prerequisite child issue scoped from `honua-server-837`) and SHOULD be
shaped to support multiple downstream protocols (3D Tiles, I3S 1.7, future
I3S 2.0). It should expose at minimum: source resolution, tileset/node-tree
traversal, per-node geometry and attribute reads, and a CRS contract. The
I3S adapter described here will be implemented against that interface.

This is called out as a *cross-protocol architectural prerequisite*, not a
blocker on this spike's deliverables.

## Relationship to sibling tickets

| Ticket | Title | Relationship |
| --- | --- | --- |
| `honua-server-837` | Hosted 3D Tiles serving | I3S serving must sit above the same scene-layer abstraction that 3D Tiles consumes. The `ISceneLayerService` boundary should be defined at `honua-server-837` and shaped to accommodate I3S adaptation. Not a hard sequencing prerequisite for the I3S service-info child issue, but a hard architectural prerequisite to avoid duplicated scene plumbing. |
| `honua-server-838` | Cesium smoke suite | Not a prerequisite for I3S. Cesium does not consume I3S, and ArcGIS Scene Viewer is the only canonical I3S consumer. I3S smoke testing requires a separate ArcGIS Scene Viewer or I3S validation harness, scoped in the dedicated conformance child issue (#6 below). The pattern of `honua-server-838` (committed fixture + automated visual smoke) is the template the I3S harness ticket should follow. |
| `honua-server-842` | 3D Tiles generation pipeline | **Hard prerequisite** for any translation-adapter I3S path. Until B3DM/PNTS generation exists, the only I3S work that can ship is a service-info / layer-descriptor JSON adapter without geometry, which has limited standalone value. Geometry-bearing child issues are sequenced strictly behind `honua-server-842`. |
| `honua-server-849` | Protected scene access envelope | **Hard prerequisite** for production I3S serving. All I3S endpoints must be gated by the same scene access envelope (auth, signed URLs, layer-level access) defined by `honua-server-849`. The spec-shape child issues can be designed in parallel, but no I3S route may be enabled in production until `honua-server-849` ships. |

## Proposed implementation sequence and child issues

The recommendation is **proceed with bounded Enterprise child issues**, in
the order below. Every child issue is `edition/enterprise`, includes
`scene.i3s.*` operation telemetry, and follows the four-file protocol
adapter pattern (`Endpoints`, `Models`, `JsonContext`, `ServiceCollectionExtensions`)
already used by `Terrain/`. None of these child issues are opened by this
spike; they are the proposed shape for follow-on grooming.

| # | Title | Effort | Edition | Depends on | Notes |
| --- | --- | --- | --- | --- | --- |
| 1 | `feat(scene/i3s): SceneServer service info and layer descriptor endpoints` | S | Enterprise | `honua-server-842` (scene-layer abstraction must exist), `honua-server-849` (auth gate) | Thin JSON projection adapter at `/rest/services/{id}/SceneServer` and `/layers/0`. No geometry, no attributes. Lives at `src/Honua.Server/Features/Protocols/SceneServer/`; core abstraction in `src/Honua.Core/Features/Scene/`. Source-generated JSON context. |
| 2 | `feat(scene/i3s): 3DObject node tree and basic geometry serving via translation adapter` | XL | Enterprise | Child #1, `honua-server-842` (B3DM pipeline) | Adds `nodepages`, `nodes/{id}`, `shared`, `geometries/0`, `textures/0`. Includes glTF → I3S geometry transcoder. **Open question**: per-request transcoding vs. bake-time generation; recommend bake-time as the default to keep tile-serving latency budgets intact, with per-request as a fallback only for low-traffic Enterprise scenarios. |
| 3 | `feat(scene/i3s): PointCloud layer type via translation adapter` | M | Enterprise | Child #1, `honua-server-842` (PNTS pipeline) | Adds PNTS → I3S point bundle conversion and the `Point`/`PointCloud` layer-type variants. |
| 4 | `feat(scene/i3s): attribute index file generation` | L | Enterprise | Child #2 | Native pipeline; no 3D Tiles analog. Adds `attributes/f_N/0` per-field binary attribute files and the per-field `statistics/f_N/0` resource. Required for ArcGIS Scene Viewer `identify` parity. Deferred until the attribute storage and indexing model is defined. |
| 5 | `feat(scene/i3s): IntegratedMesh native pipeline` | XL | Enterprise | None of the above (separate native indexing path) | Photogrammetry-mesh-only native indexing and node generation pipeline. Deferred; depends on a separate photogrammetry ingestion strategy not yet scoped in `honua-server-842`. |
| 6 | `feat(scene/i3s): I3S conformance fixture and ArcGIS Scene Viewer smoke harness` | M | Enterprise | Child #1 (must have something to test) | Commits the synthetic minimal fixture and the `Esri/i3s-spec` sample fixtures, plus an automated I3S protocol-shape validator. ArcGIS Scene Viewer smoke remains manual until a viable headless harness is available; the manual smoke runbook is part of this child. Follows the `honua-server-838` Cesium-smoke pattern for fixture management. |

## Risks and tradeoffs

- **Geometry transcoding cost.** Online glTF → I3S binary transcoding per
  tile request is on a hot path (tile-serving latency budget). Bake-time
  generation alongside the 3D Tiles pipeline avoids this but doubles output
  storage and adds pipeline complexity. Child issue #2 must pick a default
  before implementation; this matrix recommends bake-time as the default.
- **I3S spec version spread.** ArcGIS Pro 2.x targets I3S 1.6/1.7; ArcGIS
  Online's modern Scene Viewer increasingly assumes I3S 2.0 compact bundles.
  Targeting 1.7 first keeps scope bounded but means modern AGOL Scene Viewer
  may serve a "compatibility-mode" experience until 2.0 is added. This is
  acceptable for the initial Enterprise offering.
- **HLOD tree mismatch.** I3S requires a fixed-depth HLOD tree declared at
  the layer level. 3D Tiles flexible implicit/explicit trees do not map
  exactly. The translation adapter normalizes to a maximum HLOD depth at
  bake time; this can produce valid but potentially suboptimal LOD
  transitions in ArcGIS Scene Viewer.
- **Attribute indexing gap.** I3S attribute files are first-class in the
  spec and many ArcGIS workflows depend on them for identify and query.
  There is no 3D Tiles analog, so attributes require a native pipeline
  (child issue #4) regardless of the geometry strategy. Without #4, I3S
  serving has no attribute-identify parity.
- **Missing core scene abstraction.** No `ISceneLayerService` exists today.
  Without defining it before `honua-server-837` and the I3S adapter ship,
  scene plumbing will be duplicated. Surfaced as a prerequisite, not a
  blocker on this spike.
- **Enterprise scope risk.** Committing to I3S signals Esri-replacement
  ambition. If the Enterprise roadmap shifts, in-flight I3S work could
  strand. Bounded child issues with explicit gating and clear sequencing
  mitigate this.

## Open questions for review

These questions are deliberately left open by this spike. The recommendations
above assume the answers documented here; reviewers should push back on any
that are wrong before child issues are opened.

1. **I3S spec version target.** Recommendation: I3S 1.7 first, with I3S 2.0
   tracked as follow-on. Does the Enterprise customer roadmap require I3S
   2.0 (compact bundles) on the initial release?
2. **Geometry transcoding strategy.** Recommendation: bake-time generation
   alongside `honua-server-842`. Acceptable, or is per-request transcoding
   required for storage reasons?
3. **`ISceneLayerService` ownership.** Recommendation: defined as part of
   `honua-server-837`. Acceptable, or should this spike open a dedicated
   prerequisite child issue?
4. **Reference fixture source.** Recommendation: synthetic minimal fixture
   leads CI; `Esri/i3s-spec` MIT samples vendored as a secondary check.
   Acceptable, or do we want fully synthetic fixtures only?
5. **Open-core discoverability.** Recommendation: I3S routes return `403
   Feature requires Enterprise edition` (consistent with PrintingTools
   layout templates and Pro-tier spatial analytics). Acceptable, or should
   I3S be fully absent from open-core routing?

## Acceptance-criteria mapping

| Acceptance criterion (from `honua-server-843`) | Where addressed |
| --- | --- |
| Compatibility matrix covers I3S layer types, metadata, geometry, materials, attributes, LOD, and service endpoints | "Layer-type compatibility", "Metadata, attributes, and structural compatibility", "Geometry and material compatibility", "Service endpoint compatibility", "LOD and screen-space-error normalization" |
| Each feature area has a recommendation: translation adapter, native pipeline, or deferred scope | Recommendation column in every matrix table; vocabulary defined in "Status vocabulary" |
| The spike proposes child issues for implementation if moving forward is recommended | "Proposed implementation sequence and child issues" (six bounded child issues) |
| At least one public/simple I3S reference dataset is identified for future tests | "Reference fixtures and compatibility targets" (`Esri/i3s-spec` MIT samples + synthetic fixture + Philadelphia Buildings smoke target) |
| Enterprise gating notes are explicit | "Enterprise gating, licensing, and support boundary" |
| The plan states how I3S work relates to `honua-server-837`, `honua-server-838`, `honua-server-842`, `honua-server-849` | "Relationship to sibling tickets" |
| No production I3S serving is added in this spike | This page is the only artifact; no source files, endpoint registrations, or routing changes are introduced. The MVP compatibility contract row is updated to "Not implemented — Enterprise roadmap" |

## Cross-references

- Launch contract entry: [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md) — see the I3S / ArcGIS Scene Layer row.
- Sibling 3D scene tickets: `honua-server-837` (hosted 3D Tiles), `honua-server-838` (Cesium smoke), `honua-server-842` (3D Tiles generation), `honua-server-849` (protected scene envelope).
- Esri I3S 1.7 spec (OGC Community Standard 17-014r8) and the public `Esri/i3s-spec` repository at <https://github.com/Esri/i3s-spec> are the conformance references for any future implementation child.
