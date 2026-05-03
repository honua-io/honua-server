# 3D Tiles Generation Pipeline

The v1 3D Tiles generation pipeline (#842) converts a registered Honua
PostGIS feature layer into a deterministic OGC 3D Tiles 1.1 tileset that is
served through the existing hosted scene infrastructure ([scenes-3dtiles](scenes-3dtiles.md)).

This is the producer side of Honua's 3D capability. Hosted serving (#837) and
the Postgres-backed scene dataset registry (#844) are prerequisites.

## Public route

| Route | Method | Purpose |
| --- | --- | --- |
| `/api/v1/admin/scenes/generate` | POST | Convert a feature layer into a 3D Tiles tileset and register it with the scene registry. |

Admin authorization is required (the same gate the rest of the
`/api/v1/admin/*` surface uses).

The endpoint runs synchronously: small/medium datasets fit comfortably inside
an admin-request timeout. Asynchronous job tracking through a durable
`IPublishIntentStore` is intentionally deferred to a follow-on ticket.

### Request

```json
{
  "layerId": 7,
  "sceneId": "downtown-buildings",
  "displayName": "Downtown buildings",
  "description": "Photogrammetry-aligned massing model derived from buildings layer",
  "includeAttributes": ["name", "height", "year_built"],
  "maxFeatureCount": 5000,
  "cacheMaxAgeSeconds": 3600,
  "editionGate": "pro"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `layerId` | yes | Catalog id of a feature layer with a polygon, point, or linestring geometry column. |
| `sceneId` | no | Stable URL slug, validated by the canonical `SceneDatasetValidator`: 1–64 chars, must match `[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?` (lowercased server-side, no trailing hyphen). When omitted, the server derives an ASCII slug from the layer name plus an 8-char intent suffix and rejects derivations that fail the same pattern. |
| `displayName` | no | Human-readable name (≤ 128 characters); defaults to the layer name. |
| `description` | no | Optional description text. |
| `includeAttributes` | no | Allowlist of attribute fields to project into the tileset's metadata schema. Empty means all numeric/string attributes. |
| `maxFeatureCount` | no | Per-job override of the v1 50 000-feature cap; the smaller of the two values wins. |
| `cacheMaxAgeSeconds` | no | Cache directive applied to the registered scene dataset. Bounded to `[0, 86400]` (24 hours); values above the ceiling are rejected with `SCENE_OPTIONS_INVALID`. |
| `editionGate` | no | Optional licensing gate forwarded to the scene record. Must be a lowercase slug (letters, digits, hyphens). |

### Successful response (`201 Created`)

```json
{
  "sceneId": "downtown-buildings",
  "intentId": "0c9c1f3a16dc4e6ea9b8ca6f3a4c1f72",
  "serviceId": "scene:downtown-buildings",
  "tilesetUrl": "https://server.honua.io/scenes/downtown-buildings/tileset.json",
  "featureCount": 412,
  "tileCount": 1,
  "boundingRegionDegrees": [-122.5, 37.7, -122.4, 37.8],
  "geometricError": 12345.67,
  "warnings": []
}
```

Once the response returns, the tileset is discoverable via the existing
hosted serving routes:

- `GET /scenes/{sceneId}/tileset.json` — root document.
- `GET /scenes/{sceneId}/tile_0000.glb` — single tile content.

CesiumJS loads the result with no client-side URL rewriting:

```js
const tileset = await Cesium.Cesium3DTileset.fromUrl(
  "https://server.honua.io/scenes/downtown-buildings/tileset.json"
);
viewer.scene.primitives.add(tileset);
```

## Supported v1 inputs

- **Geometry kinds**: `Polygon`, `MultiPolygon`, `Point`, `MultiPoint`,
  `LineString`, `MultiLineString`. Multi-geometries are normalized to the
  first part for a single deterministic representation per feature.
- **Source CRS**: any PostGIS-supported CRS that
  `ST_Transform(geom, 4326)` can resolve. Vertices are projected to WGS-84
  (lon/lat in degrees) before being encoded as ECEF (EPSG:4978) meters in
  the GLB.
- **Z values**: `ST_Z(geom)` is preserved when present. 2D layers may opt
  into vertical extrusion via the catalog `extrusionInfo` block (#841).
  When neither Z values nor extrusion info is configured, the output is
  flat at Z=0 with a logged warning.
- **Attribute types**: `Integer`, `BigInteger`, `Double`, `Float`, and
  `String` are surfaced through the GLB's `EXT_structural_metadata` schema.
  `Integer` is encoded as `SCALAR/INT32`; `BigInteger` is encoded as
  `SCALAR/INT64` and the corresponding bufferView is 8-byte aligned per the
  EXT_structural_metadata specification. `Double` and `Float` are encoded as
  `SCALAR/FLOAT32`. Other types are omitted from the metadata table.

## Output layout

Each generation job writes one directory under
`{SceneGeneration:OutputRoot}/{sceneId}/`:

```
scenes-generated/
  downtown-buildings/
    tileset.json
    tile_0000.glb
```

`OutputRoot` defaults to `scenes-generated` (relative to the application
content root). Configure it via `appsettings.json`:

```json
{
  "SceneGeneration": {
    "OutputRoot": "/var/lib/honua/scenes-generated",
    "MaxFeatureCount": 50000,
    "GeneratorTag": "honua-3dtiles-generator/1.0"
  }
}
```

The generated tileset is automatically registered with the scene dataset
registry; no separate `POST /api/v1/admin/scenes` call is required.

### Tileset shape

- `asset.version = "1.1"`
- `asset.generator` = configured `GeneratorTag`
- `geometricError` is computed as the WGS-84 bounding-box diagonal in
  meters (rounded to 6 decimals).
- Root tile uses a `region` bounding volume in radians: `[west, south,
  east, north, minHeight, maxHeight]`.
- Each tile content is a glTF 2.0 binary (GLB) with one mesh primitive:
  - `POSITION` accessor in ECEF meters (EPSG:4978).
  - `_FEATURE_ID_0` accessor providing per-vertex feature ids
    (`EXT_mesh_features`).
  - One `propertyTable` row per feature with values for each included
    attribute (`EXT_structural_metadata`).

## Determinism guarantees

- Features are streamed in primary-key order, so the input sequence is
  stable across runs against the same source data.
- Polygon triangulation uses a fan rooted at the first ring vertex; the
  result is deterministic for any given ring.
- The GLB binary buffer is laid out in a fixed sequence: positions, feature
  ids, then property columns in declaration order.
- `tileset.json` is serialized through a source-generated
  `JsonSerializerContext` with no dictionary keys.

The unit-test suite verifies that two runs against an identical fixture
produce byte-identical `tileset.json` and `tile_0000.glb` files.

## Failure problem details

Generation failures surface stable error codes through the shared admin
problem-detail helpers:

| Code | Status | Meaning |
| --- | --- | --- |
| `SCENE_LAYER_NOT_FOUND` | 404 | The layer id does not resolve in the catalog. |
| `SCENE_LAYER_CRS_UNKNOWN` | 400 | The layer's spatial reference cannot be projected to ECEF. |
| `SCENE_UNSUPPORTED_GEOMETRY_TYPE` | 400 | A feature has a geometry kind not supported by v1. |
| `SCENE_FEATURE_LIMIT_EXCEEDED` | 400 | The layer exceeds the configured maximum feature count. |
| `SCENE_ATTRIBUTE_TYPE_UNSUPPORTED` | 400 | Reserved for future attribute-type validation surfaces. |
| `SCENE_MODEL_ASSET_INVALID` | 400 | Reserved for the future glTF/GLB model-asset path. |
| `SCENE_REGISTRATION_CONFLICT` | 409 | The requested scene id or display name collides with an existing dataset. The executor preflights this against the registry before any filesystem writes, so a duplicate request leaves any pre-existing scene assets unchanged. |
| `SCENE_OPTIONS_INVALID` | 400 | Generation options failed validation. The executor runs the canonical `SceneDatasetValidator` checks (`sceneId`, `displayName`, `cacheMaxAgeSeconds`, `editionGate`) before doing any I/O so invalid input never produces a partial output directory. |

## Limits and known v1 limitations

- **Hard 50 000 feature cap.** Layers exceeding the cap are rejected; raise
  the `SceneGeneration:MaxFeatureCount` configuration value with care.
- **Single-tile output.** v1 emits one tile per generation job and does not
  spatially partition large datasets. Enterprise-scale tiling and LOD
  optimization are deferred.
- **Fan triangulation for polygons.** Convex polygons render correctly;
  non-convex rings produce visually imperfect fills (the topology is
  still deterministic and the mesh remains parsable).
- **Inner rings ignored.** Polygons with holes have their outer ring used;
  inner ring support is deferred.
- **No client-supplied glTF/GLB model assets in v1.** The
  `SCENE_MODEL_ASSET_INVALID` error code is reserved for that future path.
- **No CityGML/IFC ingestion.** A documented future path; v1 does not
  ingest semantic city models.
- **Concurrent regeneration of the same layer is unsupported.** Each job
  produces its own scene id; the most recent registration wins on
  collision unless the operator supplied an explicit `sceneId`.

## Deferred enterprise-scale work

- Quadtree/oct-tree spatial partitioning for medium/large datasets.
- LOD generation with automatic mesh decimation.
- Streaming/incremental tile production for million-feature datasets.
- Distributed tiling for parallel pipelines.
- glTF/GLB model-asset substitution (rooftops, tree instances).
- CityGML/IFC ingestion.
- Native I3S output (informed by #843).

## Related

- [Hosted 3D Tiles serving](scenes-3dtiles.md) (#837)
- [Scene dataset registry admin API](../admin-api/scene-dataset-registry.md) (#844)
- [Extruded 3D feature layers](extruded-3d-feature-layers.md) (#841)
