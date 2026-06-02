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
- OGC API – Styles Phase 1 adapter: implemented (ADR-0048, issue #1388;
  `core`, `mapbox-styles`, `sld-10`, `sld-11`, `style-validation`, partial
  `manage-styles`). Independent style catalog (Phase 2, full POST/DELETE) is
  issue #1389.
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
regenerated on the next read after a MapLibre-only update; this regeneration
does not touch revision metadata.

A layer that has never received a PUT has `maplibre_style = NULL` in storage.
The admin GET and the public read endpoint both serve a deterministic
in-memory default for such layers (see `StyleDefaults.BuildDefaultMapLibreStyle`)
with `styleVersion: 0` and null `revisedAt` / `revisedBy` / `changeSummary` —
no read path persists this default, so repeat reads on an unstyled layer do
not bump `style_version` or stamp `style_revised_at`. The first PUT lands as
revision `1` with the caller-supplied metadata.

The schema enforces this contract at the storage layer: migration
`022_AddStyleRevisionMetadata.sql` sets `style_version DEFAULT 0` and
backfills any row where `maplibre_style IS NULL AND style_revised_at IS NULL`
to `style_version = 0` so the original `DEFAULT 1` from migration `009`
cannot make an unstyled row read as revision `1`. Updates use
`style_version = COALESCE(style_version, 0) + 1`, so the first PUT on a
newly-published row increments `0 -> 1` deterministically.

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
| `PICTURE_MARKER_PARTIAL` | A picture marker payload was preserved but not all layout hints round-trip across the per-stop set, including any `defaultSymbol` payload — non-uniform `xoffset`/`yoffset`/`angle` between stops or against `defaultSymbol` triggers the code. Also raised when a `uniqueValue` / `classBreaks` renderer mixes `esriPMS` and non-`esriPMS` (e.g. `esriSMS`) stops; the color fallback path renders the layer but cannot preserve the image metadata, so the diagnostic surfaces the dropped images. |
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
| `colorblind-safe` | Remaps the distinct fill/line/circle colors onto the `Viridis` palette in `ColorPalettes`. Identical input RGBA values map to the same palette slot within a single transform, so a `classBreaks` first-class color reused as a case fallback stays visually equal. The input alpha channel is preserved through the swap so semi-transparent fills/lines keep their opacity. Reuses the existing palette data; no new palettes are introduced. |
| `print` | Forces every present opacity property (scalar or expression) to `1.0`, every present `line-color` on `line` layers (scalar or expression) to `#000000`, and every `fill-outline-color` on `fill` layers to `#000000` for high-contrast print rendering. |

An unknown `theme` query value returns `400 Bad Request` listing the supported
profiles. A malformed *stored* MapLibre document falls back to the canonical
output without raising an error so the public read endpoint stays available.
Successful theme applications log at `Debug` (event id `6402`); individual
malformed color literals encountered during the transform skip in place and
log at `Debug` (event id `6403`) with the offending property and color value.

Theme transforms walk literal color paint properties and use an explicit
allow-list of MapLibre expression operators that have color-output positions.
The allowed operators are `case`, `match`, `step`, `interpolate`, and
`coalesce`:

- `case` — each branch output and the mandatory trailing fallback are
  rewritten; every predicate is skipped entirely.
- `match` — every output and the trailing fallback are rewritten; the input
  expression and match input labels are skipped entirely.
- `step` — the default output and every per-stop output are rewritten;
  the input expression and numeric stops are skipped entirely.
- `interpolate` — every output at indices 4, 6, 8, … is rewritten; the
  interpolation specification, input expression, and numeric stops are
  skipped entirely.
- `coalesce` — every operand is a possible color output and is rewritten
  through the same operator-aware walker, so a fallback such as
  `["get", "color"]` keeps its field name while a sibling `"#ff0000"`
  literal is themed.

Every other operator in the bounded surface that the admin write-time
normalizer accepts (`get`, `has`, `literal`, `concat`, `typeof`, `to-number`,
`to-string`, comparators (`==`, `!=`, `<`, `<=`, `>`, `>=`), `!`, `all`,
`any`, and arithmetic `+` / `-` / `*` / `/`) plus the public-read-only
`feature-state` operator and any unknown operator is skipped entirely: their
operands are property names, predicates, identifiers, string-construction
inputs, or numeric arguments that must not be rewritten as themed hex
literals just because a feature value or string operand happens to parse as
a CSS named color. Without this allow-list, expressions such as
`["concat", "red", "fish"]`, `["literal", "red"]`, or
`["==", ["get", "color"], "red"]` would have their accepted operands
rewritten under `?theme=dark|colorblind-safe`, corrupting the stored style
intent. Feature values that happen to be color-like (for example a
`uniqueValue` category equal to `"#ff0000"` reached via a `match` input
label, or a case predicate comparing against a hex literal) are therefore
preserved verbatim.

In particular, a valid data-driven binding such as `["get", "red"]` (read
the `red` feature property) keeps its property name verbatim because the
walker does not visit any positions inside `get` / `has` / `feature-state` —
without the allow-list, `"red"` would have parsed as a CSS named color and
been rewritten as a themed hex literal.

Color literals are recognized in any of the forms the admin write-time
normalizer accepts: hex (`#rgb`/`#rgba`/`#rrggbb`/`#rrggbbaa`), `rgb(...)`
and `rgba(...)` in either legacy comma syntax (`rgb(255, 0, 0)`) or CSS
Color Module Level 4 modern syntax (`rgb(255 0 0 / 0.5)`), `hsl(...)`
and `hsla(...)` in either legacy or modern syntax with optional hue
units (`deg`, `grad`, `rad`, `turn`), and the canonical CSS / X11 named
color set (`red`, `transparent`, `steelblue`, …). A stored named or
HSL color is themed exactly as if it had been emitted as the
equivalent hex literal.

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

### OGC API Styles (Phase 1 — shipped)

The OGC API – Styles Phase 1 adapter (ADR-0048, issue #1388) exposes the same
canonical per-layer style document through the OGC negotiation surface without
re-translating it. It is a thin protocol slice in
`src/Honua.Protocols.OgcApi/Styles/` mounted under `/ogc/styles`, projecting the
existing per-layer store; it reaches the internal style services through the
public `IOgcStyleProjection` abstraction (`Honua.Core`, implemented by
`OgcStyleProjection` in `Honua.Server`), mirroring the existing
`ISldStyleConverter` / `IGeoServicesStyleConverter` cross-project pattern.

| Endpoint | Method | Behavior |
|----------|--------|----------|
| `/ogc/styles` | GET | Landing links + styles list (one entry per collection that has a stored MapLibre style). |
| `/ogc/styles/conformance` | GET | Declares `core`, `mapbox-styles`, `sld-10`, `sld-11`, `style-validation`, partial `manage-styles`. |
| `/ogc/styles/openapi.json` | GET | OpenAPI document for the slice. |
| `/ogc/styles/{styleId}` | GET | Content-negotiated stylesheet: MapLibre (`application/vnd.mapbox.style+json`, default) or derived SLD 1.0/1.1 by `Accept`. |
| `/ogc/styles/{styleId}/metadata` | GET | `id`, `title`, `description`, `keywords`, `license`, `version` + stylesheet / `describedby` (schema) / `preview` links. |
| `/ogc/styles/{styleId}` | PUT | `manage-styles`: validates MapLibre via the normalizer and writes through `ILayerStyleService`. Honors `Prefer: handling=strict` and `?validate`. |
| `/ogc/styles` (POST), `/ogc/styles/{styleId}` (DELETE) | POST/DELETE | `501 Not Implemented` — standalone style CRUD arrives with the Phase 2 independent catalog (issue #1389). |

The `styleId` is the collection's stable resource name — the same identifier
used as the OGC API Features collectionId — chosen to be forward-compatible with
the Phase 2 styleId-keyed catalog. SLD encodings are derived on demand from the
canonical MapLibre style via `MapLibreToSldConverter`; the canonical store only
holds MapLibre. The collection metadata (`GET /ogc/features/collections/{id}`)
carries `rel:"style"` (the `/api/styles/{layerId}.json` alias, unchanged),
`rel:"styles"` (→ `/ogc/styles`), and `rel:"stylesheet"` (→ `/ogc/styles/{styleId}`)
links. The revision metadata fields (`styleVersion`, `revisedAt`, `revisedBy`,
`changeSummary`) back the OGC style `version` field.

## Symbolizer support matrix

| GeoServices renderer | MapLibre output | Notes |
|----------------------|-----------------|-------|
| `simple` | `circle` / `line` / `fill` (+ outline) | Picture markers (`esriPMS`) emit a `symbol` layer with metadata for image lookup. |
| `uniqueValue` | data-driven `match` expression with non-null guard | Color and picture-marker variants share the same non-null guard. Defaults route to `defaultSymbol` color (color renderer) or `defaultSymbol` image (picture-marker renderer) when present; otherwise fall back to the first stop's color (color renderer) or the first stop image (picture-marker renderer). The `defaultSymbol` payload is included in the layout uniformity check, so a divergent `xoffset`/`yoffset`/`angle` raises `PICTURE_MARKER_PARTIAL`. A renderer that mixes `esriPMS` with non-`esriPMS` stops cannot emit a clean picture-marker layer; it raises `PICTURE_MARKER_PARTIAL` and falls back to the color path. |
| `classBreaks` | data-driven `step` expression with numeric guard (`to-number` coercion + `typeof == number` case guard) | Color and picture-marker variants share the same numeric guard. Defaults route to `defaultSymbol` color (color renderer) or `defaultSymbol` image (picture-marker renderer) when present; otherwise fall back to the first class output. The `defaultSymbol` payload is included in the layout uniformity check, so a divergent `xoffset`/`yoffset`/`angle` raises `PICTURE_MARKER_PARTIAL`. A renderer that mixes `esriPMS` with non-`esriPMS` stops cannot emit a clean picture-marker layer; it raises `PICTURE_MARKER_PARTIAL` and falls back to the color path. |
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
  - `6400` - unsupported GeoServices renderer type observed (Warning).
  - `6401` - optional drawingInfo could not be resolved; metadata was
    returned without it (Warning).
  - `6402` - theme transform applied (Debug).
  - `6403` - theme transform skipped a malformed color value (Debug).
