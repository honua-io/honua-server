---
type: guide
title: "Make your first map"
description: "You'll turn a published layer into a live MapLibre map using vector tiles, TileJSON, and the server's auto-generated style in about 10 minutes."
---
# Make your first map

You'll turn a published layer into a live MapLibre map using vector tiles, TileJSON, and the server's auto-generated style in about 10 minutes.

**Prerequisites:** a published layer and its `layerId` (see [Publish your first dataset](first-dataset.md)), `$HONUA_BASE_URL`, `$HONUA_API_KEY`, `$HONUA_SERVICE`, and `$HONUA_LAYER_ID` set as that page describes, and Python 3 to serve one HTML file. Step 1 installs the pinned clients (`honua-admin` 0.1.9, `honua-sdk` 0.1.12) and the MCP Python client `mcp` 2.1.1, so this page can be run on its own.

Every published layer is automatically served as Mapbox Vector Tiles at `/tiles/{layerId}/{z}/{x}/{y}.mvt`, described by TileJSON at `/tiles/{layerId}/tile.json`, with a ready-made MapLibre style at `/api/styles/{layerId}.json` — no tile cache to build, no style to author.

## Steps

1. Set variables and allow anonymous reads on the service so the browser can fetch tiles without credentials.

The `honua_admin_services_access_policy_set` MCP tool sets a service's access policy. Call it
through the server's MCP endpoint (`POST /mcp`) with the official MCP Python client.
`$HONUA_SERVICE` is the service name the publish step exported.

```bash
python3 -m pip install 'honua-admin==0.1.9' 'honua-sdk==0.1.12' 'mcp==2.1.1'
python3 - <<'PY'
import asyncio
import os

from mcp import ClientSessionGroup
from mcp.client.session_group import StreamableHttpParameters

service = os.environ["HONUA_SERVICE"]


async def allow_anonymous():
    server = StreamableHttpParameters(
        url=f"{os.environ['HONUA_BASE_URL']}/mcp",
        headers={"X-API-Key": os.environ["HONUA_API_KEY"]},
    )
    async with ClientSessionGroup() as group:
        session = await group.connect_to_server(server)
        return await session.call_tool("honua_admin_services_access_policy_set", {
            "serviceName": service,
            "allowAnonymous": True,
        })


call = asyncio.run(allow_anonymous())
if call.is_error:
    raise SystemExit(f"access policy not set: {call.content}")
print(f"anonymous read enabled on {service}")
PY
```

MCP tool calls authenticate with the `X-API-Key` header; HTTP Basic auth is refused.
`$HONUA_BASE_URL` and `$HONUA_API_KEY` come from your quickstart install's `.env` — see
[Publish your first dataset](first-dataset.md) for the two lines that read them.

The interactive API explorer at `/docs` is served only when `HONUA_SERVE_API_DOCS=true` — it defaults on in `Development` and off in `Production`, which is what the packaged Compose profiles run, so on a default install that URL is a 404. The [admin OpenAPI document](../developer/api-specs/admin-api.json) is the client-generation contract.

2. Fetch the TileJSON. It carries the tile URL template, zoom range, data bounds, the vector layer schema, and a link to the auto-generated style.

Open `$HONUA_BASE_URL/tiles/$HONUA_LAYER_ID/tile.json` in a browser (`http://localhost:18080/tiles/1/tile.json` when the publish step printed layer id `1`), replacing `1` with `$HONUA_LAYER_ID`.

```text
{"tilejson":"3.0.0","name":"hawaii-cities","scheme":"xyz",
 "tiles":["http://localhost:18080/tiles/1/{z}/{x}/{y}.mvt"],
 "minzoom":0,"maxzoom":22,"bounds":[…],"vector_layers":[{"id":"layer",…}],
 "style":"http://localhost:18080/api/styles/1.json"}
```

3. Fetch the auto-generated MapLibre style. It is a complete MapLibre v8 style document with geometry-appropriate defaults; add `?theme=dark`, `?theme=colorblind-safe`, or `?theme=print` for variants.

Open `$HONUA_BASE_URL/api/styles/$HONUA_LAYER_ID.json` in a browser, replacing `1` with `$HONUA_LAYER_ID`. Add `?theme=dark`, `?theme=colorblind-safe`, or `?theme=print` for a variant.

4. Save this as `map.html`. It reads the TileJSON, fits the map to your data's bounds, and draws the features over an OpenStreetMap basemap (in every MVT tile the source-layer name is `layer`). Set `layerId` to `$HONUA_LAYER_ID` from the publish step. The sample below uses `1`, which is that id when the publish step printed `1`.

```bash
cat > map.html <<'EOF'
<!doctype html><html><head><meta charset="utf-8">
<script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
<link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet">
<style>html,body,#map{margin:0;height:100%}</style></head>
<body><div id="map"></div><script>
const server = 'http://localhost:18080', layerId = 1; // your HONUA_BASE_URL and layerId
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

5. Allow the page's origin. The quickstart's `compose.yaml` only allows the server's own origin, so add a second entry under `environment:` and restart:

```yaml
      Cors__AllowedOrigins__1: "http://localhost:3000"
```

```bash
docker compose up -d
```

6. Serve the page and open <http://localhost:3000/map.html>.

The process is ready when it prints `Serving HTTP on 0.0.0.0 port 3000 (http://0.0.0.0:3000/) ...`.
`PYTHONUNBUFFERED=1` makes that line appear immediately when stdout is not a terminal. Leave the process running.

<!-- doc-run: run ready-log="Serving HTTP on 0.0.0.0 port 3000 (http://0.0.0.0:3000/) ..." -->

```bash
export PYTHONUNBUFFERED=1
python3 -m http.server 3000
```

> One line with [honua-sdk-js](https://github.com/honua-io/honua-sdk-js): the SDK resolves TileJSON and styles for a layer so you can skip the manual `fetch`.

## Verify

In the browser developer tools, confirm the TileJSON and vector-tile requests return `200`. The page should show your features over the basemap, centered on the data extent.

## Troubleshoot

- **Tile or TileJSON requests return 401** — anonymous read is not enabled on the service (step 1), or the layer was published to a service other than `default`.
- **404 on `/tiles/{id}/tile.json`** — wrong `layerId`; use the `layerId` from the publish response, and confirm the layer is enabled.
- **CORS errors in the browser console** — the page's origin is not in `Cors__AllowedOrigins__*` (step 5); `file://` pages are always blocked.
- **Map loads but no features visible** — confirm `'source-layer':'layer'` is set on the style layer and that the map view covers your data's bounds.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Style maps](../guides/style/style-maps.md) — replace the defaults with your own renderers and themes
- [MapLibre web maps](../guides/connect/maplibre-web-maps.md) — production patterns for web clients
- [Publish tiles](../guides/publish/publish-tiles.md) — tile caching, PMTiles, and OGC API Tiles
