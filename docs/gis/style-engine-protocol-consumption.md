# Style Engine: Cross-Protocol Consumption

This document describes how the canonical MapLibre Style Spec v8 document
managed by the Honua style engine flows into the rendering and metadata
endpoints exposed by each protocol family. ADR-0002 picks MapLibre as the
single source of truth; this page closes the loop by explaining what each
protocol slice does with that document.

## Status

- Style engine: implemented (ticket 344)
- Theme engine: implemented (`default`, `dark`, `colorblind-safe`, `print`)
- Style revision metadata: implemented (`styleVersion`, `revisedAt`,
  `revisedBy`, `changeSummary` returned on admin GET/PUT)
- Visual diff UX in the Admin UI: deferred to a follow-up ticket

## Storage shape

```
honua.layers
    maplibre_style          JSONB  -- canonical MapLibre Style Spec v8
    geoservices_drawing_info JSONB  -- cached conversion (regenerated lazily)
    style_version            INT    -- monotonic revision counter
    style_revised_at         TIMESTAMPTZ
    style_revised_by         TEXT
    style_change_summary     TEXT   -- max 1000 chars
```

The canonical row is updated only by `PUT /api/v1/admin/metadata/layers/{layerId}/style`.
Every successful update increments `style_version`, stamps `style_revised_at`
to the server's UTC clock, and records the caller-supplied `changedBy` and
`changeSummary` fields. The cached `geoservices_drawing_info` column is
regenerated on the next read after a MapLibre-only update.

## Authoring entry points

| Operation | Endpoint | Notes |
|-----------|----------|-------|
| Submit MapLibre style | `PUT /api/v1/admin/metadata/layers/{layerId}/style` body `{ "mapLibreStyle": { ... } }` | Validated by `MapLibreStyleNormalizer`; rejected with 400 on schema errors. |
| Submit GeoServices drawingInfo | `PUT /api/v1/admin/metadata/layers/{layerId}/style` body `{ "drawingInfo": { ... } }` | Converted to MapLibre by `GeoServicesToMapLibreConverter`. Unsupported renderer/symbol types are reported in the response body via `unsupportedSymbolizers[]` with stable codes from `StyleErrorCodes`. |
| Read canonical style | `GET /api/v1/admin/metadata/layers/{layerId}/style` | Returns the canonical MapLibre document plus revision metadata. |
| Read public style | `GET /api/styles/{layerId}.json[?theme=dark|colorblind-safe|print]` | Public read endpoint with output cache; theme transforms applied deterministically per query key. |

## Unsupported symbolizers

Updates that include renderer or symbol types outside the supported set
(`simple`, `uniqueValue`, `classBreaks`) succeed with a default MapLibre
fallback and surface every dropped feature in `unsupportedSymbolizers[]`.
Each entry has the shape:

```json
{
  "code": "RENDERER_TYPE_UNSUPPORTED",
  "symbolizerType": "heatmap",
  "guidance": "Renderer type is not supported. Use 'simple', 'uniqueValue', or 'classBreaks', or submit a MapLibre style."
}
```

Stable codes are defined in `StyleErrorCodes` and must not change across
releases:

| Code | Meaning |
|------|---------|
| `RENDERER_TYPE_UNSUPPORTED` | The submitted GeoServices renderer type is outside `simple` / `uniqueValue` / `classBreaks`. |
| `SYMBOL_TYPE_UNSUPPORTED` | A nested symbol uses a type the converter cannot translate. |
| `PICTURE_MARKER_PARTIAL` | A picture marker payload was preserved but not all layout hints round-trip. |
| `RENDERER_PAYLOAD_INCOMPLETE` | The renderer object was missing required fields and was treated as default. |

## Theme engine

Theme transforms are deterministic: the same canonical style + theme always
produces the same output. They run in-memory on the GET path; the output cache
varies by the `theme` query key so themed responses share invalidation tags
with the canonical entry but do not collide.

| Theme | Behavior |
|-------|----------|
| `default` | Returns the canonical style unchanged. |
| `dark` | Converts each color paint property to HSL and inverts lightness; sets background fills to `#1a1a1a`. Hue and saturation are preserved. |
| `colorblind-safe` | Remaps the distinct fill/line/circle colors onto the `Viridis` palette in `ColorPalettes`. Reuses the existing palette data; no new palettes are introduced. |
| `print` | Forces all opacity properties to `1.0`, line colors to `#000000`, and fill outlines to black for high-contrast print rendering. |

Malformed input or unknown themes return the canonical style unchanged. The
themed body is logged at `Debug` (event id `6402`).

## Cross-protocol consumption

### Map Server (GeoServices REST `MapServer` and `FeatureServer`)

- `MapServerRequestHandlers.Legend` reads `LayerStyleDefinition.MapLibreStyleJson`
  and parses it via `StyleTranslator.ParseStyleLayers` to produce the
  GeoServices legend response.
- FeatureServer rendering uses the cached `geoservices_drawing_info`
  representation. When that cache is empty, `MapLibreToGeoServicesConverter`
  regenerates it from the canonical MapLibre document on first read and
  persists the result.
- `MapServerRequestHandlers.Export` uses the canonical MapLibre document to
  drive the raster rendering pipeline (see WMS GetMap below).

### Static Map and WMS GetMap

- `StaticMapRequestHandlers` and the OGC WMS GetMap handler resolve the
  canonical style through `ILayerStyleService.GetStyleAsync`, then pass the
  document to `RasterMapRenderingPipeline`.
- `RasterMapRenderingPipeline` walks `layers[*].paint` and `layers[*].layout`,
  evaluating any data-driven expressions against the underlying feature stream
  via `ExpressionEvaluator` (rendering uses the same MapLibre expression
  semantics as MVT tiles).
- The `?theme` query parameter is honored only on the public
  `GET /api/styles/{layerId}.json` endpoint. WMS GetMap and FeatureServer Export
  always render against the canonical style; theme-aware print exports should
  fetch the themed style via the public endpoint and pass it to a client-side
  renderer if needed.

### Vector Tiles (MVT)

- The MVT pipeline (`/tiles/{layerId}/{z}/{x}/{y}.mvt`) does not embed style
  information in the tile itself.
- Clients fetch `GET /api/styles/{layerId}.json` to retrieve the same
  canonical document used by the rendering pipelines.
- Data-driven styling expressions inside the canonical document are
  evaluated client-side by MapLibre using the source-layer name (`layer`)
  and the field names declared on the layer.
- Theme transforms applied to `/api/styles/{layerId}.json?theme=...` return a
  deterministic variant that shares the underlying `layer-styles` cache tag.

### OGC API Styles (planned)

The cross-protocol contract is designed so a future OGC API Styles slice can
expose the same canonical document through the OGC negotiation surface
without re-translating it. The revision metadata fields (`styleVersion`,
`revisedAt`, `revisedBy`, `changeSummary`) are stable enough to back an OGC
"style version" identifier or the visual-diff UX in the Admin UI.

## Symbolizer support matrix

| GeoServices renderer | MapLibre output | Notes |
|----------------------|-----------------|-------|
| `simple` | `circle` / `line` / `fill` (+ outline) | Picture markers (`esriPMS`) emit a `symbol` layer with metadata for image lookup. |
| `uniqueValue` | data-driven `match` expression with non-null guard | Defaults route to `defaultSymbol` color or transparent fallback. |
| `classBreaks` | data-driven `step` expression with numeric guard | Defaults route to `defaultSymbol` color when present. |
| Other types (`heatmap`, `dotDensity`, `vectorField`, …) | Default style + `unsupportedSymbolizers[]` | Reported with code `RENDERER_TYPE_UNSUPPORTED`. |

## Operational notes

- All style writes go through `ILayerStyleCatalog`, which is wrapped by
  `CachingLayerStyleCatalog` so the in-memory layer cache is invalidated on
  every revision write.
- Output cache entries for `/api/styles/{layerId}.json` and the themed
  variants share the `layer-styles` tag; the admin update endpoint calls
  `OutputCacheInvalidationService.InvalidateLayerAsync` to flush the entire
  set on every revision.
- Telemetry events:
  - `6400` - unsupported GeoServices renderer type observed.
  - `6402` - theme transform applied (Debug).
  - `6403` - theme transform skipped a malformed color value (Debug).
