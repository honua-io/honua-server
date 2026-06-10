# Make your first map

You'll turn a published layer into a live MapLibre map using vector tiles, TileJSON, and the server's auto-generated style in about 10 minutes.

**Prerequisites:** a published layer and its `layerId` (see [Publish your first dataset](first-dataset.md)), the admin key, and Python 3 to serve one HTML file.

Every published layer is automatically served as Mapbox Vector Tiles at `/tiles/{layerId}/{z}/{x}/{y}.mvt`, described by TileJSON at `/tiles/{layerId}/tile.json`, with a ready-made MapLibre style at `/api/styles/{layerId}.json` — no tile cache to build, no style to author.

## Steps

1. Set variables and allow anonymous reads on the service so the browser can fetch tiles without credentials.

```bash
export HONUA=http://localhost:8080
export KEY=quickstart-admin-password
export LAYER_ID=1   # layerId from your publish response
curl -s -X PUT -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"allowAnonymous":true}' "$HONUA/api/v1/admin/services/default/access-policy"
```

2. Fetch the TileJSON. It carries the tile URL template, zoom range, data bounds, the vector layer schema, and a link to the auto-generated style.

```bash
curl -s "$HONUA/tiles/$LAYER_ID/tile.json"
```

```text
{"tilejson":"3.0.0","name":"quickstart-points","scheme":"xyz",
 "tiles":["http://localhost:8080/tiles/1/{z}/{x}/{y}.mvt"],
 "minzoom":0,"maxzoom":22,"bounds":[…],"vector_layers":[{"id":"layer",…}],
 "style":"http://localhost:8080/api/styles/1.json"}
```

3. Fetch the auto-generated MapLibre style. It is a complete MapLibre v8 style document with geometry-appropriate defaults; add `?theme=dark`, `?theme=colorblind-safe`, or `?theme=print` for variants.

```bash
curl -s "$HONUA/api/styles/$LAYER_ID.json"
```

4. Save this as `map.html`. It reads the TileJSON, fits the map to your data's bounds, and draws the features over an OpenStreetMap basemap (in every MVT tile the source-layer name is `layer`).

```bash
cat > map.html <<'EOF'
<!doctype html><html><head><meta charset="utf-8">
<script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
<link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet">
<style>html,body,#map{margin:0;height:100%}</style></head>
<body><div id="map"></div><script>
const server = 'http://localhost:8080', layerId = 1; // your layerId
fetch(`${server}/tiles/${layerId}/tile.json`).then(r => r.json()).then(tj => {
  const map = new maplibregl.Map({container:'map', style:{version:8,
    sources:{
      osm:{type:'raster',tiles:['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],tileSize:256,attribution:'© OpenStreetMap'},
      honua:{type:'vector',tiles:tj.tiles,minzoom:tj.minzoom,maxzoom:tj.maxzoom}},
    layers:[
      {id:'osm',type:'raster',source:'osm'},
      {id:'features',type:'circle',source:'honua','source-layer':'layer',
       paint:{'circle-radius':7,'circle-color':'#2D69A5','circle-stroke-color':'#fff','circle-stroke-width':2}}]}});
  if (tj.bounds) map.fitBounds([[tj.bounds[0],tj.bounds[1]],[tj.bounds[2],tj.bounds[3]]],{padding:60,maxZoom:13});
});
</script></body></html>
EOF
```

For line layers change the layer `type` to `line` (paint: `line-color`, `line-width`); for polygons use `fill` (paint: `fill-color`, `fill-opacity`).

5. Serve the page from an allowed CORS origin and open <http://localhost:3000/map.html>.

```bash
python3 -m http.server 3000
```

> One line with [honua-sdk-js](https://github.com/honua-io/honua-sdk-js): the SDK resolves TileJSON and styles for a layer so you can skip the manual `fetch`.

## Verify

```bash
curl -s -o /dev/null -w "%{http_code}\n" "$HONUA/tiles/$LAYER_ID/0/0/0.mvt"
```

```text
200
```

The browser shows your features drawn over the basemap, centered on the data extent.

## Troubleshoot

- **Tile or TileJSON requests return 401** — anonymous read is not enabled on the service (step 1), or the layer was published to a service other than `default`.
- **404 on `/tiles/{id}/tile.json`** — wrong `layerId`; use the `layerId` from the publish response, and confirm the layer is enabled.
- **CORS errors in the browser console** — serve `map.html` from an origin listed in `Cors__AllowedOrigins__*` (the quickstart override allows `http://localhost:3000`); `file://` pages are always blocked.
- **Map loads but no features visible** — confirm `'source-layer':'layer'` is set on the style layer and that the map view covers your data's bounds.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Style maps](../guides/style/style-maps.md) — replace the defaults with your own renderers and themes
- [MapLibre web maps](../guides/connect/maplibre-web-maps.md) — production patterns for web clients
- [Publish tiles](../guides/publish/publish-tiles.md) — tile caching, PMTiles, and OGC API Tiles
