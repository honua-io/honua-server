# Build a web map with MapLibre

Render a Honua layer in the browser from vector tiles (MVT) using TileJSON metadata and the server-generated MapLibre style.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

Honua serves per-layer vector tiles and metadata:

- Tiles: `/tiles/{layerId}/{z}/{x}/{y}.mvt` (XYZ scheme, source layer name `layer`)
- TileJSON 3.0: `/tiles/{layerId}/tile.json` (includes tile URL, bounds, zoom range, and a `style` link)
- MapLibre style: `/api/styles/{layerId}.json` (layer-id alias; the canonical style route is `/ogc/styles/{styleId}`, which serves MapLibre and SLD via content negotiation)

## Steps

1. Confirm the layer's TileJSON responds:

   Open `http://localhost:8080/tiles/{layerId}/tile.json` in a browser, substituting the published layer id. The response contains the vector-tile URL template, bounds, and supported zoom range.

2. Save this as `map.html` (replace the layer id if yours differs):

   ```html
   <!DOCTYPE html>
   <html>
   <head>
     <meta charset="utf-8" />
     <script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
     <link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet" />
     <style> #map { position: absolute; inset: 0; } </style>
   </head>
   <body>
     <div id="map"></div>
     <script>
       const base = "http://localhost:8080";
       const layerId = 1;
       const map = new maplibregl.Map({
         container: "map",
         center: [0, 0],
         zoom: 2,
         style: {
           version: 8,
           sources: {
             honua: { type: "vector", url: `${base}/tiles/${layerId}/tile.json` }
           },
           layers: [
             {
               id: "honua-fill",
               type: "circle", // use "fill" or "line" to match your geometry type
               source: "honua",
               "source-layer": "layer"
             }
           ]
         }
       });
     </script>
   </body>
   </html>
   ```

   Alternatively, skip the hand-written style and pass the served style document directly: `style: \`${base}/api/styles/${layerId}.json\``.

3. Serve the file and open it in a browser:

   ```bash
   python -m http.server 8000
   # open http://localhost:8000/map.html
   ```

### Other web mapping libraries

- **Leaflet** — no native vector-tile support: use raster WMS (`L.tileLayer.wms("http://localhost:8080/ogc/services/{serviceId}/wms", { layers: "..." })`) or a vector-tile plugin such as `Leaflet.VectorGrid` pointed at the `.mvt` URL template.
- **OpenLayers** — use `ol/layer/VectorTile` with an `ol/source/VectorTile` whose `url` is `http://localhost:8080/tiles/{layerId}/{z}/{x}/{y}.mvt` (MVT format).
- **SDK** — `honua-sdk-js` ships a MapLibre runtime plus an ArcGIS compatibility layer if you'd rather not wire sources by hand.

## Verify

Open `/tiles/{layerId}/tile.json` and verify the returned `tiles` template. Then load the page and confirm in browser developer tools that a request such as `/tiles/{layerId}/0/0/0.mvt` returns `200` with a Mapbox vector-tile content type.

The page should render features over an empty background; the browser dev-tools network tab should show `.mvt` requests returning 200.

## Troubleshoot

- **Blank map, tile requests fail with CORS errors** — the page origin is not allowed by the server's CORS configuration; serve the page from an allowed origin or adjust the CORS settings. See [troubleshooting](../deploy/troubleshooting.md).
- **Tiles return 404** — wrong layer id, or the layer is not tile-enabled; `/tiles/{layerId}/tile.json` should return metadata, not an error.
- **Tiles load but nothing draws** — the style's `source-layer` must be `layer`, and the layer `type` (`circle`/`line`/`fill`) must match the geometry type; also check the TileJSON `bounds` and pan there.
- **Features missing at low zoom** — requests outside the configured zoom range return no data; respect `minzoom`/`maxzoom` from the TileJSON.

## Next steps

- [Style maps](../style/style-maps.md)
- [Publish tiles](../publish/publish-tiles.md)
- [Query features](../query-analyze/query-features.md)
