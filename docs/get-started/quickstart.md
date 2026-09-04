# Go from zero to a map in your browser

> This quickstart follows the Honua 2026.1 GA **single-tenant** path. Multi-tenant
> operation is Preview/trial only for non-production evaluation; do not use customer
> production data. It has no GA, availability, performance, durability, or SLO
> commitment. See [Tenancy support](../guides/deploy/tenancy.md).

You'll have Honua running in Docker with a published dataset rendered in a browser map in about 10 minutes.

**Prerequisites:** Docker with Compose v2, `git`, GitHub CLI authenticated with package-read access, and Python 3.11 or later.

## Steps

1. Clone the repository.

<!-- docs-validation:quickstart.clone mode=skip reason=the-harness-runs-inside-a-checkout -->
```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
```

2. Build the server image from this checkout, then start the stack (PostGIS, Redis, and Honua Server). The build consumes the repository's GitHub Packages dependency without storing your token in an image layer. The bootstrap stores random per-install PostgreSQL and MinIO passwords in a private `.env` file and preserves them on restart. All published ports bind to loopback unless you explicitly change `HONUA_BIND_ADDRESS`. Keep `.env` with your persistent volumes; changing its passwords does not rotate credentials in an existing database. The repo-root Compose file includes development-only defaults for the admin password, connection-encryption key, Redis control-plane connection, Gate migration policy, and browser origin used below.

<!-- docs-validation:quickstart.start mode=run -->
```bash
export HONUA_BASE_URL="${HONUA_BASE_URL:-http://localhost:8080}"
repo_root="${HONUA_REPO_ROOT:-.}"
if [ -z "${HONUA_SERVER_IMAGE:-}" ]; then
  GITHUB_ACTOR="${GITHUB_ACTOR:-$(gh api user --jq .login)}" GH_TOKEN=$(gh auth token) \
    bash "${repo_root}/scripts/docker/build-with-github-packages.sh" -t honua-server:local "${repo_root}"
fi
python3 "${repo_root}/scripts/docker/quickstart.py"
```

3. Wait until the server reports ready.

The preceding command waits for every Compose health check. Confirm the services are healthy, then open <http://localhost:8080/healthz/ready> in a browser. The page should say `Ready`.

<!-- docs-validation:quickstart.ready mode=run -->
```bash
docker compose ps
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

5. Create a small PostGIS table with three points. The Compose database is local development infrastructure; this seed uses its local container socket.

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

6. Install the supported Python control-plane and data-plane clients.

<!-- docs-validation:quickstart.sdk mode=run -->
```bash
python3 -m venv --without-pip .quickstart-venv
python3 -m pip --python .quickstart-venv/bin/python install \
  "honua-sdk @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-sdk" \
  "honua-admin @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-admin"
. .quickstart-venv/bin/activate
```

7. Register the Compose database as a connection (publishing reads tables through named connections).

<!-- docs-validation:quickstart.connection mode=run -->
```bash
python3 - <<'PY'
import os
from pathlib import Path

import subprocess

from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key="quickstart-admin-password") as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="local",
        host="postgres",
        port=5432,
        database_name="honua_dev",
        username="honua_user",
        password=subprocess.check_output(
            ["docker", "compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD"],
            text=True,
        ).strip(),
        ssl_mode="Prefer",
        ssl_required=False,
    ))
    Path(".quickstart-connection-id").write_text(connection.connection_id)
PY
```

8. Publish the discovered table as a layer and note the `layerId` in the response.

<!-- docs-validation:quickstart.publish mode=run -->
```bash
python3 - <<'PY'
import os
from pathlib import Path

from honua_admin import HonuaAdminClient, PublishLayerRequest

connection_id = Path(".quickstart-connection-id").read_text()
with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key="quickstart-admin-password") as admin:
    layer = admin.publish_layer(connection_id, PublishLayerRequest(
        schema="honua_data",
        table="quickstart_points",
        layer_name="quickstart-points",
        geometry_column="geometry",
        geometry_type="Point",
        srid=4326,
        primary_key="id",
        fields_list=["id", "name"],
        service_name="quickstart",
    ))
    Path(".quickstart-layer-id").write_text(str(layer.layer_id))
    print(layer)
PY
```

9. Save this as `map.html`, substitute the published layer ID, then serve it and open <http://localhost:3000/map.html>. The request hook supplies the Compose quickstart's development-only admin key; use a user-scoped credential instead for a deployed server.

<!-- docs-validation:quickstart.map mode=run -->
```bash
cat > map.html <<'EOF'
<!doctype html><html><head><meta charset="utf-8">
<script src="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.js"></script>
<link href="https://unpkg.com/maplibre-gl@4/dist/maplibre-gl.css" rel="stylesheet">
<style>html,body,#map{margin:0;height:100%}</style></head>
<body><div id="map"></div><script>
const layerId = __LAYER_ID__;
new maplibregl.Map({container:'map',center:[-122.41,37.79],zoom:12,
 transformRequest:url => url.startsWith('__HONUA_BASE_URL__')
   ? {url,headers:{'X-API-Key':'quickstart-admin-password'}} : {url},
 style:{version:8,
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
python3 - <<'PY'
import os
from pathlib import Path

from honua_sdk import HonuaClient

layer_id = int(Path(".quickstart-layer-id").read_text())
with HonuaClient(os.environ["HONUA_BASE_URL"], api_key="quickstart-admin-password") as client:
    result = client.query_features("quickstart", layer_id)
    assert len(result["features"]) == 3
    print("Verified 3 quickstart features")
PY
```

The command should report three verified features, and the browser map should show three blue circles over San Francisco.

## Troubleshoot

- **Tiles return 401 in the browser** — confirm the request hook still carries the local quickstart key and the layer was published to the `quickstart` service.
- **Blank map and CORS errors in the browser console** — the page must be served from `http://localhost:3000`, not opened as a `file://` URL. Use `HONUA_DEV_CORS_ORIGIN` before `docker compose up -d` if you serve the page from another origin.
- **Console is not needed after enabling the profile** - use `HONUA_CONSOLE_REPLICAS=0 docker compose up -d`; Redis still starts because durable jobs, proposals, and workflow state use it.
- **Contract migration is gated on an existing database** - the quickstart sets `HONUA_CONTRACT_APPLY_POLICY=Gate`. Fresh installs still provision fully; for an upgrade with pending contract scripts, approve one run with the nonce printed by the migration safety error and unset it afterward.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Publish your first dataset](first-dataset.md) — the import → publish → query flow in detail
- [Make your first map](first-map.md) — TileJSON, auto-generated styles, and MapLibre
- [All guides](../guides/README.md)

For an existing installation using the old sample passwords, rotate the PostgreSQL role password and MinIO root password explicitly, then save the matching values in `.env` before restarting. Do not delete existing data volumes to reset credentials. The bootstrap refuses old weak values instead of silently replacing them.
