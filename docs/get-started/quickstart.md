# Go from zero to a map in your browser

You'll have Honua running in Docker with a published dataset rendered in a browser map in about 10 minutes.

**Prerequisites:** Docker with Compose v2, `git`, `curl`, and Python 3 (only used to serve one HTML file).

## Steps

1. Clone the repository.

```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
```

2. Start the stack (PostGIS, Redis, and Honua Server, built from source - the first run takes a few minutes). The repo-root compose file includes development-only defaults for the admin password, connection-encryption key, Redis control-plane connection, Gate migration policy, and browser origin used below.

```bash
docker compose up -d
```

3. Wait until the server reports ready.

```bash
curl http://localhost:8080/healthz/ready
```

4. Optional Console dashboard: once a compatible `honua-console` image is published, start the profiled Console service and open <http://localhost:5174/operate>. The same service also serves <http://localhost:5174/operate/health> and <http://localhost:5174/operate/copilot>. Console binds to the local server with the quickstart admin key, so admin-only Operate reads work without another deploy step.

```bash
HONUA_CONSOLE_IMAGE=ghcr.io/honua-io/honua-console:replace-with-compatible-tag docker compose --profile console up -d
```

For headless local runs after enabling the profile, leave Redis enabled and disable only the dashboard:

```bash
HONUA_CONSOLE_REPLICAS=0 docker compose up -d
```

5. Create a small GeoJSON file to import.

```bash
cat > points.geojson <<'EOF'
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Ferry Building"},"geometry":{"type":"Point","coordinates":[-122.3937,37.7955]}},
 {"type":"Feature","properties":{"name":"Coit Tower"},"geometry":{"type":"Point","coordinates":[-122.4058,37.8024]}},
 {"type":"Feature","properties":{"name":"Painted Ladies"},"geometry":{"type":"Point","coordinates":[-122.4330,37.7762]}}]}
EOF
```

6. Import it. Admin endpoints authenticate with the `X-API-Key` header carrying the admin password.

```bash
curl -s -H "X-API-Key: quickstart-admin-password" \
  -F "file=@points.geojson" -F "TableName=quickstart_points" \
  http://localhost:8080/api/v1/admin/import/upload
```

7. Register the compose database as a connection (publishing reads tables through named connections).

```bash
curl -s -H "X-API-Key: quickstart-admin-password" -H "Content-Type: application/json" \
  -d '{"name":"local","host":"postgres","port":5432,"databaseName":"honua_dev","username":"honua_user","password":"honua_password","sslRequired":false,"sslMode":"Prefer"}' \
  http://localhost:8080/api/v1/admin/connections
```

8. Publish the imported table as a layer (imports land in the `honua_data` schema) and note the `layerId` in the response.

```bash
curl -s -H "X-API-Key: quickstart-admin-password" -H "Content-Type: application/json" \
  -d '{"schema":"honua_data","table":"quickstart_points","layerName":"quickstart-points","srid":4326}' \
  http://localhost:8080/api/v1/admin/connections/local/layers
```

9. Allow anonymous reads on the `default` service so the browser can fetch tiles without a key.

```bash
curl -s -X PUT -H "X-API-Key: quickstart-admin-password" -H "Content-Type: application/json" \
  -d '{"allowAnonymous":true}' \
  http://localhost:8080/api/v1/admin/services/default/access-policy
```

10. Save this as `map.html` (if your `layerId` from step 8 was not `1`, change the first line of the script), then serve it and open <http://localhost:3000/map.html>.

```bash
cat > map.html <<'EOF'
<!doctype html><html><head><meta charset="utf-8">
<script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
<link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet">
<style>html,body,#map{margin:0;height:100%}</style></head>
<body><div id="map"></div><script>
const layerId = 1; // from the publish response in step 8
new maplibregl.Map({container:'map',center:[-122.41,37.79],zoom:12,style:{version:8,
 sources:{
  osm:{type:'raster',tiles:['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],tileSize:256,attribution:'© OpenStreetMap'},
  honua:{type:'vector',tiles:['http://localhost:8080/tiles/'+layerId+'/{z}/{x}/{y}.mvt']}},
 layers:[
  {id:'osm',type:'raster',source:'osm'},
  {id:'points',type:'circle',source:'honua','source-layer':'layer',
   paint:{'circle-radius':8,'circle-color':'#2D69A5','circle-stroke-color':'#fff','circle-stroke-width':2}}]}});
</script></body></html>
EOF
python3 -m http.server 3000
```

## Verify

```bash
curl -s http://localhost:8080/tiles/1/tile.json
```

```text
{"tilejson":"3.0.0","name":"quickstart-points","scheme":"xyz","tiles":["http://localhost:8080/tiles/1/{z}/{x}/{y}.mvt"],…}
```

In the browser you should see three blue circles over San Francisco.

## Troubleshoot

- **Tiles return 401 in the browser** — step 9 (anonymous read) was skipped, or the publish used a different service name than `default`.
- **Blank map and CORS errors in the browser console** — the page must be served from `http://localhost:3000`, not opened as a `file://` URL. Use `HONUA_DEV_CORS_ORIGIN` before `docker compose up -d` if you serve the page from another origin.
- **Console is not needed after enabling the profile** - use `HONUA_CONSOLE_REPLICAS=0 docker compose up -d`; Redis still starts because durable jobs, proposals, and workflow state use it.
- **Contract migration is gated on an existing database** - the quickstart sets `HONUA_CONTRACT_APPLY_POLICY=Gate`. Fresh installs still provision fully; for an upgrade with pending contract scripts, approve one run with `HONUA_APPROVE_CONTRACT_MIGRATIONS=true` and unset it afterward.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Publish your first dataset](first-dataset.md) — the import → publish → query flow in detail
- [Make your first map](first-map.md) — TileJSON, auto-generated styles, and MapLibre
- [All guides](../guides/README.md)
