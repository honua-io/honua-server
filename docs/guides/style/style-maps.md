---
type: guide
title: "Style map layers"
description: "Set a layer's symbology once, as a MapLibre Style Spec v8 document, so vector-tile clients, WMS and static-map rendering, and ArcGIS drawingInfo read the same document. Feature labeling is not a served capability."
resource: "honua://capability/styling.defaults"
resources:
  - "honua://capability/styling.auto-suggest"
---
# Style map layers

Set a layer's symbology once, as a MapLibre Style Spec v8 document, so vector-tile clients, WMS and static-map rendering, and ArcGIS `drawingInfo` read the same document. Feature labeling is not part of that contract.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), a published layer ([publish layers](../publish/publish-layers.md)), and an admin API key ([authentication](../secure/authentication.md)).

Honua stores exactly one canonical style per layer — MapLibre JSON. Esri `drawingInfo` and SLD are accepted as inputs and derived as outputs. Fill, line, and circle symbology is what those renderers share. Also available in Honua Console — UI guide coming soon.

## Feature labeling is not served

The styling capability keys are `styling.defaults`, `styling.auto-suggest`, and `styling.ogc-api-styles`. There is no labeling key, and no route serves label classes.

- Server-side raster rendering (`SkiaFeatureDrawing.RenderStyledFeature`, used by WMS GetMap, static maps, and MapServer export) paints only `fill`, `line`, and `circle`. A MapLibre `symbol` layer, including `layout.text-field`, is not drawn.
- Default FeatureServer `drawingInfo` sets `labelingInfo` to null (`FeatureServerMetadataHandlers.BuildDefaultDrawingInfoV2`). `MapLibreToGeoServicesConverter.BuildDrawingInfo` writes a renderer, or a picture marker from `icon-image`. It never writes `labelingInfo`.
- SLD import stores a `TextSymbolizer` whose label is an `ogc:PropertyName` as a `symbol` layer `text-field` (`SldToMapLibreConverter.BuildTextLayer`). `SldParser.ParseTextSymbolizer` drops `LabelPlacement`. Tests of that import check the stored JSON only.
- ArcGIS source `labelingInfo` is recorded as migration diagnostics (`MigrationManifestTranslator.BuildResourceLabelClassDiagnostics`). Apply does not write a label style or `labelingInfo` from those diagnostics. A simple field token classified `automated` can still be reported as construct disposition `migrated`.
- When a stored style already contains a `symbol` layer, VectorTileServer points `glyphs` at one bundled sans-serif face (Basic Latin and Latin-1). That face is for a client that implements text layout. Default styles omit symbol layers, and the server does not generate label classes.
- Visual certification `CERT-RNDR-LBL-01` has no substantiating lane.

A MapLibre client may paint `text-field` from the style document it fetches. That is the client's drawing, not a Honua labeling capability, and not Esri `labelingInfo`.

## Steps

1. Read the layer's current style. Layers that have never been styled return a deterministic default with `styleVersion: 0`:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `GET /api/v1/admin/metadata/layers/{layerId}/style`.

2. Update the style with a MapLibre document. The body is validated against the MapLibre v8 schema (400 on errors), and each successful write increments `styleVersion`:

   Run `PUT /api/v1/admin/metadata/layers/{layerId}/style` with this body, replacing the host and layer id in the tile URL:

   ```json
   {
     "mapLibreStyle": {
       "version": 8,
       "sources": {
         "layer": {
           "type": "vector",
           "tiles": ["http://localhost:8080/tiles/1/{z}/{x}/{y}.mvt"]
         }
       },
       "layers": [
         {
           "id": "fill",
           "type": "fill",
           "source": "layer",
           "source-layer": "layer",
           "paint": { "fill-color": "#2D69A5", "fill-opacity": 0.6 }
         }
       ]
     },
     "changedBy": "mike",
     "changeSummary": "blue fill"
   }
   ```

   The same endpoint also accepts `{ "drawingInfo": { ... } }` with an Esri `simple`, `uniqueValue`, or `classBreaks` renderer; it is converted to MapLibre, and anything that cannot be translated is reported in `unsupportedSymbolizers[]` in the response.

3. Ask the server to suggest a style instead of writing one by hand. The suggestion is advisory — apply it with the PUT above:

   Run `POST /api/v1/admin/metadata/layers/{layerId}/suggest-style` with `{"preferredMethod":"Quantile","preferredPalette":"Viridis","classCount":5}`.

   The response contains a ready-to-apply `mapLibreStyle`, a matching `drawingInfo`, and legend metadata. All request fields are optional (`preferredField`, `preferredMethod` of `EqualInterval`/`Quantile`/`NaturalBreaks`/`UniqueValue`, `preferredPalette` of `Viridis`/`CartoBold`/`RdBu`, `classCount` 2–12). Field-based classification requires the Pro `styling.auto-suggest` entitlement; Community editions get geometry-aware defaults.

4. Serve themed variants from the public style endpoint. Themes are derived on read — the stored style is untouched:

   Open `http://localhost:8080/api/styles/{layerId}.json?theme=dark` in a browser.

   Supported themes are `default`, `dark`, `colorblind-safe`, and `print`; an unknown value returns 400. The layerId-keyed path is a deprecated alias of the OGC API Styles surface — `GET /ogc/styles/{styleId}` serves the same document (and derived SLD via `Accept` negotiation), where `styleId` is the layer's collection id.

5. Point clients at the one style. Nothing else to configure per protocol:

   - MapLibre/MVT clients fetch `GET /api/styles/{layerId}.json` and render tiles from `/tiles/{layerId}/{z}/{x}/{y}.mvt` client-side.
   - WMS GetMap, static maps, and MapServer `export` render server-side from the canonical document (themes are not applied on these raster paths).
   - FeatureServer clients receive a `drawingInfo` renderer derived from fill, line, and circle paint. `labelingInfo` is not derived. See [Feature labeling is not served](#feature-labeling-is-not-served).

## Verify

Run `GET /api/v1/admin/metadata/layers/{layerId}/style` again in the explorer.

Expected (trimmed): the document you wrote plus revision metadata.

```json
{ "mapLibreStyle": { "version": 8, "layers": [ { "id": "fill" } ] },
  "styleVersion": 1, "revisedBy": "mike", "changeSummary": "blue fill" }
```

## Troubleshoot

- **400 on PUT** — the MapLibre document failed schema validation; the response body names the offending property. Fix the document rather than retrying.
- **200 but `unsupportedSymbolizers[]` is non-empty** — a submitted `drawingInfo` used a renderer outside `simple`/`uniqueValue`/`classBreaks` (code `RENDERER_TYPE_UNSUPPORTED`); the layer falls back to a default style. Submit MapLibre directly for those cases.
- **`?theme=` returns 400** — only `default`, `dark`, `colorblind-safe`, and `print` are valid theme values.
- **Suggestion ignores `preferredField`** — field-based classification needs the Pro `styling.auto-suggest` entitlement; Community responses say so in `observations`.
- **Style changes do not show up in tiles** — caches are tag-invalidated on write, but browsers may cache `/api/styles/{layerId}.json`; hard-reload the client. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Import SLD styles](import-sld-styles.md)
- [Connect MapLibre web maps](../connect/maplibre-web-maps.md)
- [Publish tiles](../publish/publish-tiles.md)
