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

The conformance plan targets the **Esri I3S community specification 1.7**
for the MeshPyramids, Points, and Building profiles, which corresponds to
**OGC I3S Community Standard 1.3** (document `17-014r9`). The Point Cloud
Scene Layer profile is on its own version cadence (community spec `2.0`
maps to the same OGC 1.3 standard) and is folded into the same target.
Rationale:

- The 1.7 / OGC 1.3 cohort is the version most broadly supported across
  ArcGIS Pro 2.x/3.x, ArcGIS Enterprise 10.9+, and the ArcGIS Online Scene
  Viewer.
- Newer per-profile versions (1.8/1.9/1.10 for MeshPyramids and Points,
  2.1 for Point Cloud) are documented in the upstream Esri repository and
  introduce incremental features — `timeInfo`, `rangeInfo`, point cloud
  `Extract` — that are tracked here as follow-on work, not initial scope.
  These newer versions are not yet rolled into the OGC standard.

Each I3S profile evolves on its own release cycle in the Esri community
specification, so any shared `ISceneLayerService` abstraction (see
"Prerequisite abstractions" below) MUST avoid baking 1.7-specific framing
into its public surface.

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
| Bounding volumes | MBS in `indexCRS` (e.g. lon, lat, height-meters, radius-meters in global mode); OBB in the same frame, with rotation interpreted by the layer's `normalReferenceFrame` (ENU or ECEF) | OBB / sphere derived from Honua tile transforms (ECEF) | Moderate — requires explicit reframe to `vertexCRS` + `normalReferenceFrame` | Translation adapter |
| LOD selection | Screen-space distance formula (`maxScreenThreshold` / `maxScreenThresholdSQ`) | 3D Tiles `geometricError` (screen-space-error driven) | Moderate — different formulas, both screen-space-driven | Normalizable in translation adapter |
| Attribute storage info | Layer-level field declarations (name, type, encoding) | Per-feature property schema in tileset | Easy — schema projection | Translation adapter |
| Per-node attribute files | Separate binary attribute files per field per node (`attributes/f_N/0`) for identify/query | Per-feature properties in tile (no separate binary store) | Hard — no 3D Tiles analog; required for `identify` parity | Native pipeline; Enterprise-deferred |
| Statistics summary | `statistics/f_N/0` per-field summary | Honua attribute statistics live in catalog metadata | Moderate — projection over catalog stats | Translation adapter |
| Field domains | Coded-value and range domains per field | Honua catalog domains (FeatureServer parity) | Easy — JSON projection over existing domain model | Translation adapter |

## Geometry and material compatibility

| Feature area | I3S concept | Honua equivalent | Translation feasibility | Recommendation |
| --- | --- | --- | --- | --- |
| Geometry encoding | Esri-defined binary `geometries/0` (interleaved buffer with positions, normals, UVs, colors, feature ids, region ids) | glTF binary buffers in B3DM (from `honua-server-842`) | Hard — binary layout transcoding required | Native pipeline OR online transcoder; sequencing TBD per child issue |
| Geometry compression | Optional Draco (per `compressedAttributes` in 1.7+); newer per-profile compact bundle work tracked separately by Esri | glTF buffers (Draco optional in `honua-server-842`) | Hard — compression format and bundling differ | Native pipeline OR online transcoder |
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

I3S 1.7 separates two coordinate concerns:

- **`indexCRS` and `vertexCRS`** describe how positions are stored. In
  **global mode** (the default for `SceneServer` services consumed by
  ArcGIS Scene Viewer) `indexCRS` is geographic — typically WGS 84
  (EPSG:4326) or CGCS2000 longitude/latitude/elevation. Per-vertex
  positions are encoded in `vertexCRS` as small offsets from the node's
  `mbs` center (also in `indexCRS`); local mode allows a projected
  `vertexCRS` for layers whose source is intrinsically projected.
- **`normalReferenceFrame`** describes how vertex normals — and how the
  oriented bounding box (`obb`) rotation — are interpreted. Common values
  are `east-north-up` (ENU) anchored on the node's MBS center, and
  `earth-centered` (ECEF). This is a reference-frame concern for shading
  and OBB orientation, not a position-storage concern.

The Honua 3D Tiles plan (`honua-server-842`) bakes tile transforms in
**ECEF** with glTF vertex buffers in tile-local coordinates. This is **not
the same vertex-storage contract as I3S** — treating the cross-format
positioning translation as a no-op is incorrect. A future I3S translation
adapter owns the conversion from Honua's tile-local positions into the
layer's `vertexCRS` plus per-node MBS offset, and owns picking a
`normalReferenceFrame` consistent with how Honua tile transforms were
authored.

CRS handling rules for any future translation adapter:

- Pick the I3S coordinate mode and `vertexCRS` at translation time:
  - **Global mode** (`vertexCRS = WGS84`, longitude/latitude/elevation)
    for layers whose source covers geographic extents.
  - **Local mode** (`vertexCRS` = a registered projected CRS) for layers
    whose source is intrinsically projected and whose ArcGIS consumers
    can resolve the declared CRS.
- Reject layers whose source CRS cannot be transformed to the chosen
  `vertexCRS` (return `422 Unprocessable Entity` with a structured
  problem response, mirroring Terrain).
- Vertical datum and unit assumptions follow the Terrain pattern: source
  band/feature elevations are assumed to be meters when no vertical unit
  is declared, and `verticalUnit` / `verticalDatum` are reported nullable
  in service metadata when unknown.
- Bounding volume conversion (Honua OBB → I3S MBS) must compute the
  smallest enclosing sphere on the OBB corners. The resulting MBS is
  reported in the layer's `indexCRS`; in global mode that is
  `[lon_deg, lat_deg, height_m, radius_m]` (radius is a scalar in
  meters). The I3S `obb` is reported with `center`, `halfSize`, and
  `quaternion` interpreted in the layer's `normalReferenceFrame`, so the
  adapter MUST declare `normalReferenceFrame` explicitly per layer to
  keep ArcGIS Scene Viewer shading correct.

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

### Schema and specification reference

The upstream **`Esri/i3s-spec` GitHub repository**
(<https://github.com/Esri/i3s-spec>) is the authoritative source for I3S
schema text, profile READMEs, JSON definition files, and worked examples
embedded in the spec documents. The repository's specification text is
licensed **Creative Commons Attribution-NoDerivs (CC BY-ND)**. CC BY-ND
permits redistribution with attribution but **does not permit derivative
works**, so the spec text MUST NOT be modified, paraphrased into Honua
docs, or repackaged. Linking and short attributed quotes are acceptable.

The upstream repository does not ship a vendorable corpus of `3DObject` /
`IntegratedMesh` scene fixtures: top-level `format/` contains the spec
documents and `images/` only, and the docs tree carries profile READMEs
and worked examples rather than complete scene-layer test data
(verified 2026-05-01).

### CI fixture (committed test data)

The CI fixture is a **Honua-authored synthetic minimal fixture**: a small
`3DObject` scene with schematically-valid I3S 1.7 JSON and stub geometry
buffers, generated from a deterministic seed. It validates protocol shape
without any third-party dependency or license entanglement, and would live
under `tests/fixtures/scene/i3s/` once the conformance harness ticket is
opened. This is the only committed fixture for CI gating.

If a vendorable real-world fixture becomes available with terms compatible
with this repository (Apache 2.0 source-code license), the conformance
harness child issue may add it as a secondary check; until then, no
external I3S corpus is vendored.

### Real-world manual smoke target

Esri's publicly hosted **Philadelphia Buildings** I3S Scene Layer (ArcGIS
Online) is the canonical real-world interop target for manual smoke testing
in ArcGIS Scene Viewer. It exercises the `3DObject` layer type, multi-LOD
nodes, and attribute identify. The dataset is referenced **by URL only,
not vendored**; ArcGIS Online layer terms govern interactive use, and
those terms vary per layer, so the conformance harness child issue MUST
re-confirm the layer's terms before any automated probing. Manual smoke
is appropriate because automated ArcGIS Scene Viewer harnessing is out of
scope for the initial Enterprise offering and requires a non-public Esri
toolchain.

### Recommended starter fixture

Lead with the **synthetic minimal fixture** for CI gating: it has no
external dependency, is fully deterministic, and avoids the CC BY-ND /
ArcGIS Online terms entanglements above. Treat the upstream `Esri/i3s-spec`
repository as a schema and worked-example reference (linked, attributed,
not vendored), and keep the Philadelphia Buildings layer as the manual
ArcGIS Scene Viewer smoke target only.

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
- **Third-party licensing**: the `Esri/i3s-spec` repository is referenced
  as a schema/spec source only; its specification text is **CC BY-ND** and
  MUST NOT be modified, paraphrased, or repackaged as derivative
  documentation. Quoted excerpts must carry upstream attribution. No
  fixtures from that repository are vendored. The Philadelphia Buildings
  smoke target is referenced by URL only, not vendored, and the relevant
  ArcGIS Online layer terms must be re-confirmed before any automated
  probing.

## Prerequisite abstractions

This spike surfaces a missing core abstraction: `Honua.Core` has no
`ISceneLayerService` (or equivalent scene-layer-source interface). Without
it, both `honua-server-837` (hosted 3D Tiles serving) and a future I3S
adapter risk duplicating scene indexing, caching, and telemetry logic
inline in their respective protocol features.

The interface SHOULD be defined as part of `honua-server-837` (or as a
prerequisite child issue scoped from `honua-server-837`) and SHOULD be
shaped to support multiple downstream protocols (3D Tiles and the
multiple Esri I3S profiles, including future per-profile version
upgrades). It should expose at minimum: source resolution,
tileset/node-tree traversal, per-node geometry and attribute reads, and a
CRS contract. The I3S adapter described here will be implemented against
that interface.

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
| 6 | `feat(scene/i3s): I3S conformance fixture and ArcGIS Scene Viewer smoke harness` | M | Enterprise | Child #1 (must have something to test) | Commits the Honua-authored synthetic minimal fixture as the sole committed CI corpus and adds an automated I3S protocol-shape validator. The upstream `Esri/i3s-spec` repository is referenced for schema/spec text only (CC BY-ND; not vendored). ArcGIS Scene Viewer smoke remains manual until a viable headless harness is available; the manual smoke runbook is part of this child. Follows the `honua-server-838` Cesium-smoke pattern for fixture management. |

## Risks and tradeoffs

- **Geometry transcoding cost.** Online glTF → I3S binary transcoding per
  tile request is on a hot path (tile-serving latency budget). Bake-time
  generation alongside the 3D Tiles pipeline avoids this but doubles output
  storage and adds pipeline complexity. Child issue #2 must pick a default
  before implementation; this matrix recommends bake-time as the default.
- **I3S per-profile version spread.** Each I3S profile (MeshPyramids,
  Points, Building, Point Cloud) ships its own version cadence. ArcGIS
  Pro 2.x targets the 1.6/1.7 cohort; recent ArcGIS Pro and ArcGIS Online
  Scene Viewer releases also accept the newer profile versions
  (1.8–1.10 / 2.1) and may take advantage of features such as `timeInfo`,
  `rangeInfo`, and per-profile bundle improvements when present.
  Targeting the 1.7 / OGC 1.3 cohort first keeps scope bounded; modern
  AGOL Scene Viewer remains compatible against this cohort but does not
  receive the newer-version features until those follow-on profile
  upgrades land. This is acceptable for the initial Enterprise offering.
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

1. **I3S spec version target.** Recommendation: target the Esri community
   1.7 / OGC I3S 1.3 cohort first, with newer per-profile versions
   (1.8–1.10 for MeshPyramids/Points/Building, 2.1 for Point Cloud)
   tracked as follow-on. Does the Enterprise customer roadmap require any
   newer-profile features (e.g. `timeInfo`, `rangeInfo`) on the initial
   release?
2. **Geometry transcoding strategy.** Recommendation: bake-time generation
   alongside `honua-server-842`. Acceptable, or is per-request transcoding
   required for storage reasons?
3. **`ISceneLayerService` ownership.** Recommendation: defined as part of
   `honua-server-837`. Acceptable, or should this spike open a dedicated
   prerequisite child issue?
4. **Reference fixture source.** Recommendation: the Honua synthetic
   minimal fixture is the only committed CI fixture. The upstream
   `Esri/i3s-spec` repository is a CC BY-ND specification reference, not
   a vendorable fixture corpus, so no third-party I3S fixtures are
   committed. Acceptable, or should we invest in sourcing a separately
   licensed real-world fixture before the conformance harness ships?
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
| At least one public/simple I3S reference dataset is identified for future tests | "Reference fixtures and compatibility targets" — the Honua synthetic minimal fixture is the committed CI dataset; the public `Esri/i3s-spec` repository is the schema/spec reference; the publicly hosted Philadelphia Buildings layer is the manual ArcGIS Scene Viewer smoke target |
| Enterprise gating notes are explicit | "Enterprise gating, licensing, and support boundary" |
| The plan states how I3S work relates to `honua-server-837`, `honua-server-838`, `honua-server-842`, `honua-server-849` | "Relationship to sibling tickets" |
| No production I3S serving is added in this spike | This page is the only artifact; no source files, endpoint registrations, or routing changes are introduced. The MVP compatibility contract row is updated to "Not implemented — Enterprise roadmap" |

## Cross-references

- Launch contract entry: [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md) — see the I3S / ArcGIS Scene Layer row.
- Sibling 3D scene tickets: `honua-server-837` (hosted 3D Tiles), `honua-server-838` (Cesium smoke), `honua-server-842` (3D Tiles generation), `honua-server-849` (protected scene envelope).
- The Esri I3S community specification (`Esri/i3s-spec`, <https://github.com/Esri/i3s-spec>, CC BY-ND) and the corresponding **OGC I3S Community Standard 1.3** (document `17-014r9`) are the conformance references for any future implementation child. Esri community spec 1.7 (MeshPyramids/Points/Building) and 2.0 (PointClouds) both map to OGC I3S 1.3 in the upstream synchronization table.
