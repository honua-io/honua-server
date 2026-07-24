# Styles

Reference for managing layer styles. The canonical style document is MapLibre Style Spec v8 JSON; the server back-generates a GeoServices `drawingInfo` snapshot from it so MapServer and FeatureServer renderers stay in sync.

All admin endpoints require admin authentication — see [Authentication](../../guides/secure/authentication.md).

## Layer style read and write

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/metadata/layers/{layerId}/style` | Get the layer style (MapLibre + cached `drawingInfo` + revision metadata) |
| PUT | `/api/v1/admin/metadata/layers/{layerId}/style` | Update the layer style; accepts optional `changedBy` and `changeSummary`, reports `unsupportedSymbolizers[]` with stable codes |

The MapLibre payload must be a valid Style Spec v8 document with at least one layer; an empty `layers` array returns `400`. Layers that omit `source` default to the auto-injected tile source `layer-{layerId}` (source-layer `layer`). `changeSummary` is capped at 1000 characters. Unsupported symbolizer codes are `RENDERER_TYPE_UNSUPPORTED`, `SYMBOL_TYPE_UNSUPPORTED`, `PICTURE_MARKER_PARTIAL`, and `RENDERER_PAYLOAD_INCOMPLETE`; the request still succeeds with a best-effort fallback.

In the authorized [API explorer](../openapi-and-explorer.md), run `PUT /api/v1/admin/metadata/layers/{layerId}/style` with this body:

```json
{
  "mapLibreStyle": {
    "version": 8,
    "sources": {},
    "layers": [
      {
        "id": "parcels-fill",
        "type": "fill",
        "paint": { "fill-color": "#2D69A5", "fill-opacity": 0.4 }
      }
    ]
  },
  "changeSummary": "Initial parcels style"
}
```

## SLD import and export

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/metadata/layers/{layerId}/style/import-sld` | Convert an SLD/SE 1.0 or 1.1 XML document to MapLibre style JSON and store it (1 MiB body cap) |
| GET | `/api/v1/admin/metadata/layers/{layerId}/style/export-sld` | Export the stored MapLibre style as an `application/xml` SLD 1.0 document; diagnostic count in `X-Sld-Diagnostic-Count` |

Run `POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld` and use `parcels.sld` as the XML request body.

## Style suggestions

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/metadata/layers/{layerId}/suggest-style` | Generate a suggested style for a layer from its schema and data |

Run `POST /api/v1/admin/metadata/layers/{layerId}/suggest-style` with `{}`.

## Public style fetch and themes

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/styles/{layerId}.json` | Public MapLibre style fetch; optional `?theme=default\|dark\|colorblind-safe\|print` applies a deterministic theme transform |

The output cache varies per theme; the admin update endpoint invalidates every variant on each revision.

## OGC API Styles

Named styles are also exposed through the standards-based OGC API Styles surface:

| Method | Path | Purpose |
|---|---|---|
| GET | `/ogc/styles` | List styles |
| GET | `/ogc/styles/{styleId}` | Fetch a style |
| GET | `/ogc/styles/{styleId}/metadata` | Fetch style metadata |
| POST | `/ogc/styles` | Create a style |
| PUT | `/ogc/styles/{styleId}` | Replace a style |
| DELETE | `/ogc/styles/{styleId}` | Delete a style |

## Related guides

- [Style maps](../../guides/style/style-maps.md) — how MVT, MapServer, and WMS consume the canonical style document
- [Import SLD styles](../../guides/style/import-sld-styles.md)
