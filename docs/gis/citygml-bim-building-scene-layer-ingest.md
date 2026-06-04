# CityGML / BIM Building Scene Layer Ingest

This page documents the CityGML → 3D Tiles ingest pipeline with Building Scene
Layer (BSL) semantics added for `honua-server#1207`. It covers the supported
CityGML subset, how the building/storey/component hierarchy and attributes map
onto the existing Honua scene pipeline, and the bounded follow-ups deferred from
this first slice.

## What this slice delivers

A **pure-managed CityGML building reader** plus a **Building Scene Layer
builder** that maps parsed building geometry and semantics onto Honua's existing
[3D Tiles generation pipeline](scene-generation.md):

- `CityGmlReader` (`Honua.Core.Features.Scene.Bim`) parses `bldg:Building`
  features, their boundary surfaces, and the generic/CityGML attributes attached
  at the building and surface levels into a `CityGmlModel` carrying the
  building → storey → component hierarchy.
- `BuildingSceneLayerBuilder` projects each boundary surface to the WGS-84
  ellipsoid (via a caller-supplied geo-transform, mirroring the point-cloud
  pipeline), emits one `SceneFeature` polygon per surface, and reuses
  `GeometryTileBuilder` + `TilesetDocumentWriter` to produce a deterministic,
  servable `tileset.json` plus one GLB tile.
- BSL semantics (building id, storey id, surface type, **discipline / sub-layer**,
  component id, and the parsed generic attributes) are carried **per feature**
  through the existing `EXT_mesh_features` + `EXT_structural_metadata` glTF path,
  so identify flows attribute hits back to the correct building component and
  discipline.

The output is byte-stable: features keep document order, BSL attribute columns
are emitted in a fixed schema order, and the GLB/tileset writers are
deterministic, so identical input produces identical bytes across runs.

## Supported CityGML subset

| Aspect | Supported in this slice |
| --- | --- |
| CityGML versions | 1.0 and 2.0 (namespace-tolerant; matches on XML local names) |
| Feature classes | `bldg:Building`, `bldg:BuildingPart` |
| Boundary surfaces | `WallSurface`, `RoofSurface`, `GroundSurface`, `ClosureSurface`, `CeilingSurface`, `FloorSurface`, `OuterCeilingSurface`, `OuterFloorSurface`, `InteriorWallSurface` |
| Geometry | Exterior `gml:LinearRing` rings at any LOD (`lod1`/`lod2` multi-surfaces); `gml:posList` and `gml:pos` coordinate encodings |
| Building semantics | `gml:name`, `bldg:storeysAboveGround`, `bldg:storeysBelowGround` |
| Attributes | `gen:stringAttribute` / `gen:intAttribute` / `gen:doubleAttribute` / `gen:genericAttribute` at building and surface level (nested `<gen:value>` or direct text) |
| CRS | Document CRS captured from the `gml:Envelope` `srsName`; coordinates are **not** transformed in-reader — the builder takes a projection delegate |

### Discipline / sub-layer mapping

Each boundary surface is classified into a BIM discipline (the Building Scene
Layer sub-layer) so AEC clients can filter by discipline:

| CityGML surface type | Discipline / sub-layer |
| --- | --- |
| `GroundSurface`, `FloorSurface`, `CeilingSurface`, `OuterFloorSurface`, `OuterCeilingSurface` | `Structural` |
| `WallSurface`, `RoofSurface`, `ClosureSurface`, `InteriorWallSurface` | `Architectural` |

The discipline, surface type, building id, storey id, and component id are each
emitted as a dedicated `bsl_*` metadata property on every feature.

## Limits and non-goals (this slice)

- **CityGML only.** IFC and native BIM (`.ifc`, vendor BIM) are **not** parsed.
  IFC is a large STEP/EXPRESS schema with no mainstream pure-managed reader and
  is deferred (see follow-ups).
- **Storey hierarchy is synthetic.** CityGML 2.0 has no first-class storey, so a
  single whole-building storey is synthesised. Per-room/per-storey decomposition
  from `bldg:Room` is a follow-up.
- **Exterior rings only.** Interior rings (holes) are skipped; surfaces are
  fan-triangulated by the shared `GeometryTileBuilder` (correct for convex
  rings, deterministic for non-convex — the same documented v1 limitation as the
  feature-layer scene path).
- **No textures/appearances.** CityGML `app:Appearance` (textures, materials) is
  not read.
- **No in-reader CRS transform.** A projected-CRS document needs the caller to
  supply an inverse-projection geo-transform; geographic documents pass through.
- **No ingest endpoint yet.** This slice ships the reader + builder library and
  unit tests. Wiring it behind an admin ingest executor + endpoint (mirroring
  the feature-layer publish executor) is a tracked follow-up so it lands with the
  full endpoint/operation registry + proof-ledger gates.

## Deferred follow-ups

- **IFC / native BIM reader** — `.ifc` STEP parsing and property-set mapping.
- **Ingest executor + admin endpoint** — `POST` ingest path that streams an
  uploaded CityGML document through the reader/builder, registers the resulting
  tileset in the scene dataset registry, and gates on the Enterprise edition.
- **Storey decomposition** — derive real storeys from `bldg:Room` /
  `bldg:storey` so the BSL level sub-layer reflects the model.
- **Interior rings + robust triangulation** — honour holes and use a proper
  polygon triangulator shared with the feature-layer scene path.
- **Appearances** — CityGML textures/materials → glTF PBR materials.
- **Quadtree LOD** — large city models should partition through the existing
  `SceneQuadtreePartitioner` rather than emit a single tile.
```
