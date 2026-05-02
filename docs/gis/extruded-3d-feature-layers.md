# Extruded 3D feature layers (v1)

Honua surfaces simple height-driven extrusion metadata on feature layers so
2D footprint data can drive client-side 3D rendering and downstream 3D
Tiles generation without breaking existing 2D feature services. This is a
metadata-only slice — Honua does not generate meshes or 3D Tiles in this
release. Mesh generation arrives in [`honua-server-842`](https://github.com/honua-io/honua-server/issues/842).

## What v1 covers

- Per-layer extrusion configuration stored as catalog metadata.
- A typed `extrusionInfo` block on the GeoServices FeatureServer layer
  metadata response (`GET /rest/services/{serviceId}/FeatureServer/{layerId}`).
- Field-resolution and unit validation surfaced through the shared
  problem-detail pipeline with stable, machine-readable error codes.
- Byte-for-byte 2D compatibility: when no extrusion is configured the
  response omits `extrusionInfo` entirely.

## What v1 intentionally defers

- 3D Tiles or I3S generation from the extrusion contract.
- Complex solids, parametric roofs, BIM/IFC semantics, or mesh editing.
- Admin UI screens for configuring extrusion (catalog JSON only).
- New mobile or .NET / JS / Python SDK surface — clients consume the
  existing FeatureServer layer metadata payload.

## Configuration

Extrusion is a property on the layer's catalog metadata blob, alongside
`timeInfo` and the access policy. It is persisted as JSONB on
`honua.layers.metadata` and parsed through `CatalogJsonContext` so
deserialization is AOT-safe in published builds.

```json
{
  "extrusion": {
    "heightField": "building_height_m",
    "baseHeightField": "ground_elevation_m",
    "unit": "meters",
    "defaultHeight": 3.0,
    "materialHint": "concrete"
  }
}
```

| Property | Required | Description |
| --- | --- | --- |
| `heightField` | yes | Numeric layer field that drives extrusion height. Must reference an `Integer`, `BigInteger`, `Double`, or `Float` field. |
| `baseHeightField` | no | Optional numeric field for base elevation. Same type rules as `heightField`. |
| `unit` | no | Vertical unit. One of `meters` (default), `feet`, `usSurveyFeet`. |
| `defaultHeight` | no | Fallback height when `heightField` is null. Must be `>= 0`. |
| `materialHint` | no | Free-form hint passed through to downstream 3D generation; not interpreted by this server. |

## Wire format

When `extrusion` is configured, the layer metadata response gains an
`extrusionInfo` block:

```json
{
  "id": 0,
  "name": "buildings",
  "type": "Feature Layer",
  "geometryType": "esriGeometryPolygon",
  "extrusionInfo": {
    "enabled": true,
    "heightField": "building_height_m",
    "baseHeightField": "ground_elevation_m",
    "unit": "meters",
    "defaultHeight": 3.0,
    "materialHint": "concrete"
  }
}
```

When `extrusion` is null or missing, `extrusionInfo` is omitted entirely
(the source-generated FeatureServer JSON context omits null properties on
serialization). Existing 2D clients see the same response they did before
the extrusion slice landed.

## Validation

Validation runs on every layer metadata request whenever a layer has
extrusion configured. Failures return HTTP `422 Unprocessable Entity`
through the shared `StandardErrorHelpers` pipeline; the response body
carries one or more stable error codes from `ExtrusionErrorCodes`:

| Code | Meaning |
| --- | --- |
| `EXTRUSION_HEIGHT_FIELD_MISSING` | `heightField` was missing or empty. |
| `EXTRUSION_HEIGHT_FIELD_NOT_FOUND` | `heightField` does not exist on the layer. |
| `EXTRUSION_HEIGHT_FIELD_TYPE_INVALID` | `heightField` is not a supported numeric type. |
| `EXTRUSION_BASE_FIELD_NOT_FOUND` | `baseHeightField` does not exist on the layer. |
| `EXTRUSION_BASE_FIELD_TYPE_INVALID` | `baseHeightField` is not a supported numeric type. |
| `EXTRUSION_UNIT_UNRECOGNIZED` | `unit` is not a recognized value. |
| `EXTRUSION_NEGATIVE_DEFAULT_HEIGHT` | `defaultHeight` is negative. |

Codes are part of the public contract and stable across releases.

## Determinism for downstream generation

The wire-level `extrusionInfo` is computed deterministically from the
catalog metadata: field names, units, default height, and material hint
flow through unchanged. `honua-server-842` (3D Tiles generation) consumes
this metadata as the canonical input — there is no normalization or
heuristic step between catalog config and downstream consumers.

## Related references

- [Hosted 3D Tiles scenes](./scenes-3dtiles.md) — serving already-built
  3D Tiles, complementary to v1 extrusion.
- [I3S compatibility matrix](./i3s-compatibility-matrix.md) — production
  I3S serving is out of scope for v1 extrusion.
