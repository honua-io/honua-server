# Go from zero to a map in your browser

You'll have Honua running in Docker with a published dataset rendered in a browser map in about 10 minutes.

**Prerequisites:** Docker with Compose v2, `git`, GitHub CLI authenticated with package-read access, `curl`, Python 3, and `jq`.

## Steps

1. Clone the repository.

<!-- docs-validation:quickstart.clone mode=skip reason=the-harness-runs-inside-a-checkout -->
```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
```

2. Build the server image from this checkout, then start the stack (PostGIS, Redis, and Honua Server). The build consumes the repository's GitHub Packages dependency without storing your token in an image layer. The repo-root Compose file includes development-only defaults for the admin password, connection-encryption key, Redis control-plane connection, Gate migration policy, and browser origin used below.

<!-- docs-validation:quickstart.start mode=run -->
```bash
export HONUA_BASE_URL="${HONUA_BASE_URL:-http://localhost:8080}"
repo_root="${HONUA_REPO_ROOT:-.}"
GITHUB_ACTOR="${GITHUB_ACTOR:-$(gh api user --jq .login)}" GH_TOKEN=$(gh auth token) \
  bash "${repo_root}/scripts/docker/build-with-github-packages.sh" -t honua-server:local "${repo_root}"
docker compose up -d --no-build
```

3. Wait until the server reports ready.

Run the following command, then open <http://localhost:8080/healthz/ready> in a browser. Continue when the page says `Ready`.

<!-- docs-validation:quickstart.ready mode=run -->
```bash
docker compose ps
until [ "$(curl --silent --fail "${HONUA_BASE_URL}/healthz/ready")" = "Ready" ]; do
  sleep 2
done
```

4. Optional Console dashboard: once a compatible `honua-console` image is published, start the profiled Console service and open <http://localhost:5174/operate>. The same service also serves <http://localhost:5174/operate/health> and <http://localhost:5174/operate/copilot>. Console binds to the local server with the quickstart admin key, so admin-only Operate reads work without another deploy step.

<!-- docs-validation:quickstart.console mode=skip reason=optional-profile-requires-a-compatible-image -->
```bash
HONUA_CONSOLE_IMAGE=ghcr.io/honua-io/honua-console:replace-with-compatible-tag docker compose --profile console up -d
```

For headless local runs after enabling the profile, leave Redis enabled and disable only the dashboard:

<!-- docs-validation:quickstart.console-headless mode=skip reason=optional-console-profile-only -->
```bash
HONUA_CONSOLE_REPLICAS=0 docker compose up -d
```

5. Create a small PostGIS table with three points. The Compose database is local development infrastructure, so this seed uses its documented development credentials directly.

<!-- docs-validation:quickstart.sample-data mode=run -->
```bash
docker compose exec -T postgres psql -U honua_user -d honua_dev <<'SQL'
CREATE SCHEMA IF NOT EXISTS honua_data AUTHORIZATION honua_user;
CREATE TABLE honua_data.quickstart_points (
  id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  name text NOT NULL,
  geometry geometry(Point, 4326) NOT NULL
);
INSERT INTO honua_data.quickstart_points (name, geometry) VALUES
  ('Ferry Building', ST_SetSRID(ST_Point(-122.3937, 37.7955), 4326)),
  ('Coit Tower', ST_SetSRID(ST_Point(-122.4058, 37.8024), 4326)),
  ('Painted Ladies', ST_SetSRID(ST_Point(-122.4330, 37.7762), 4326));
ANALYZE honua_data.quickstart_points;
SQL
```

6. Register the Compose database as a connection (publishing reads tables through named connections), then discover the sample table.

<!-- docs-validation:quickstart.connection mode=run -->
```bash
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  -H 'Content-Type: application/json' \
  --data '{"name":"local","host":"postgres","port":5432,"databaseName":"honua_dev","username":"honua_user","password":"honua_password","sslMode":"Prefer","sslRequired":false}' \
  "${HONUA_BASE_URL}/api/v1/admin/connections" | tee quickstart-connection.json
jq -er '.data.connectionId' quickstart-connection.json > .quickstart-connection-id
connection_id=$(cat .quickstart-connection-id)
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  "${HONUA_BASE_URL}/api/v1/admin/connections/${connection_id}/tables" | tee quickstart-tables.json >/dev/null
jq -e '[.tables[] | select(.table | endswith("quickstart_points"))] | length == 1' quickstart-tables.json
```

7. Publish the discovered table as a layer and note the `layerId` in the response.

<!-- docs-validation:quickstart.publish mode=run -->
```bash
connection_id=$(cat .quickstart-connection-id)
schema=$(jq -r 'first(.tables[] | select(.table | endswith("quickstart_points"))).schema' quickstart-tables.json)
table=$(jq -r 'first(.tables[] | select(.table | endswith("quickstart_points"))).table' quickstart-tables.json)
geometry_column=$(jq -r 'first(.tables[] | select(.table | endswith("quickstart_points"))).geometryColumn' quickstart-tables.json)
jq -n \
  --arg schema "${schema}" \
  --arg table "${table}" \
  --arg geometryColumn "${geometry_column}" \
  '{schema:$schema,table:$table,layerName:"quickstart-points",geometryColumn:$geometryColumn,geometryType:"Point",srid:4326,primaryKey:"id",fields:["id","name"],serviceName:"quickstart",enabled:true}' \
  > quickstart-publish.json
curl --fail --silent --show-error \
  -H 'X-API-Key: quickstart-admin-password' \
  -H 'Content-Type: application/json' \
  --data @quickstart-publish.json \
  "${HONUA_BASE_URL}/api/v1/admin/connections/${connection_id}/layers" | tee quickstart-layer.json
jq -er '.data.layerId' quickstart-layer.json > .quickstart-layer-id
```

8. Allow anonymous reads on the `quickstart` service so the browser can fetch tiles without a key.

<!-- docs-validation:quickstart.anonymous-read mode=run -->
```bash
curl --fail --silent --show-error \
  -X PUT \
  -H 'X-API-Key: quickstart-admin-password' \
  -H 'Content-Type: application/json' \
  --data '{"allowAnonymous":true}' \
  "${HONUA_BASE_URL}/api/v1/admin/services/quickstart/access-policy"
```

9. Save this as `map.html`, substitute the published layer ID, then serve it and open <http://localhost:3000/map.html>.

<!-- docs-validation:quickstart.map mode=run -->
```bash
cat > map.html <<'EOF'
<!doctype html><html><head><meta charset="utf-8">
<script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
<link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet">
<style>html,body,#map{margin:0;height:100%}</style></head>
<body><div id="map"></div><script>
const layerId = __LAYER_ID__;
new maplibregl.Map({container:'map',center:[-122.41,37.79],zoom:12,style:{version:8,
 sources:{
  osm:{type:'raster',tiles:['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],tileSize:256,attribution:'© OpenStreetMap'},
  honua:{type:'vector',tiles:['__HONUA_BASE_URL__/tiles/'+layerId+'/{z}/{x}/{y}.mvt']}},
 layers:[
  {id:'osm',type:'raster',source:'osm'},
  {id:'points',type:'circle',source:'honua','source-layer':'layer',
   paint:{'circle-radius':8,'circle-color':'#2D69A5','circle-stroke-color':'#fff','circle-stroke-width':2}}]}});
</script></body></html>
EOF
sed -i "s/__LAYER_ID__/$(cat .quickstart-layer-id)/" map.html
sed -i "s|__HONUA_BASE_URL__|${HONUA_BASE_URL}|" map.html
```

<!-- docs-validation:quickstart.map-server mode=skip reason=interactive-long-running-process -->
```bash
python3 -m http.server 3000
```

## Verify

<!-- docs-validation:quickstart.verify mode=run -->
```bash
layer_id=$(cat .quickstart-layer-id)
curl --fail --silent --show-error "${HONUA_BASE_URL}/rest/services/quickstart/FeatureServer" | jq -e '.layers | length == 1'
curl --fail --silent --show-error "${HONUA_BASE_URL}/rest/services/quickstart/FeatureServer/${layer_id}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true" | jq -e '.features | length == 3'
curl --fail --silent --show-error "${HONUA_BASE_URL}/tiles/${layer_id}/tile.json" | jq -e '.tiles | length > 0'
```

The commands should print `true`, and the browser map should show three blue circles over San Francisco.

## Troubleshoot

- **Tiles return 401 in the browser** — step 9 (anonymous read) was skipped, or the publish used a different service name than `quickstart`.
- **Blank map and CORS errors in the browser console** — the page must be served from `http://localhost:3000`, not opened as a `file://` URL. Use `HONUA_DEV_CORS_ORIGIN` before `docker compose up -d` if you serve the page from another origin.
- **Console is not needed after enabling the profile** - use `HONUA_CONSOLE_REPLICAS=0 docker compose up -d`; Redis still starts because durable jobs, proposals, and workflow state use it.
- **Contract migration is gated on an existing database** - the quickstart sets `HONUA_CONTRACT_APPLY_POLICY=Gate`. Fresh installs still provision fully; for an upgrade with pending contract scripts, approve one run with `HONUA_APPROVE_CONTRACT_MIGRATIONS=true` and unset it afterward.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Publish your first dataset](first-dataset.md) — the import → publish → query flow in detail
- [Make your first map](first-map.md) — TileJSON, auto-generated styles, and MapLibre
- [All guides](../guides/README.md)
