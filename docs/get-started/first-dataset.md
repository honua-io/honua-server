---
type: guide
title: "Publish your first dataset"
description: "Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients."
---
# Publish your first dataset

See the [Honua vocabulary](../concepts/domain/README.md) for the meanings of Connection, Dataset, Layer, Service, Folder, and Environment.

Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients.

**Prerequisites:** a running server with an admin password set (steps 1–4 of the [quickstart](quickstart.md)), Node.js with `npx` (`@honua/sdk-js` 0.5.1), and Python 3.11+ with the pinned clients `honua-admin` 0.3.3 and `honua-sdk` 0.3.3 (step 3 installs them).

> **Shell.** Every block on this page is `bash` — heredocs, `export`, and `python3`. On Windows run
> them in WSL or Git Bash, not PowerShell, and note that a bare `python3` there resolves to the
> Microsoft Store stub; use `python` or a venv interpreter. The [quickstart](quickstart.md) shows
> the PowerShell equivalents where they differ.

> **Base URL and admin key.** Steps 1–4 of the [quickstart](quickstart.md) write a generated
> admin password into `secrets/honua_admin_password` and publish the server on the
> `HONUA_HTTP_PORT` that `.env` sets (`18080`). Take both from there rather than retyping the
> literals below:
>
> ```bash
> cd <your quickstart install directory>
> export HONUA_BASE_URL="http://localhost:$(grep '^HONUA_HTTP_PORT=' .env | cut -d= -f2)"
> export HONUA_API_KEY="$(cat secrets/honua_admin_password)"
> ```
>
> Every command below uses `$HONUA_BASE_URL` and `$HONUA_API_KEY`.

## 1. Create a small dataset

```bash
cat > cities.geojson <<'EOF'
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Honolulu","population":343421},"geometry":{"type":"Point","coordinates":[-157.8583,21.3069]}},
 {"type":"Feature","properties":{"name":"Hilo","population":44186},"geometry":{"type":"Point","coordinates":[-155.0868,19.7074]}}]}
EOF
```

## 2. Import the file

Upload `cities.geojson` with the Honua CLI (`@honua/sdk-js@0.5.1`), which calls the server's
`uploadImportFile` admin operation (`POST /api/v1/admin/import/upload`) using `HONUA_ADMIN_KEY`.

```bash
HONUA_ADMIN_KEY="$HONUA_API_KEY" npx --yes -p @honua/sdk-js@0.5.1 \
  honua admin import uploadImportFile \
  --body '{"file":"@cities.geojson","tableName":"hawaii_cities","targetSrid":4326,"overwriteExisting":true}' \
  --yes > import.json
python3 - <<'PY'
import json

result = json.load(open("import.json"))
if not result.get("success"):
    raise SystemExit(f"import failed: {result}")
print(json.dumps({
    "success": result["success"],
    "tableName": result["tableName"],
    "featureCount": result["featureCount"],
    "schema": result.get("schema") or "honua_data",
    "physicalTableName": result.get("physicalTableName") or result["tableName"],
}))
PY
```

A successful import responds with:

```json
{"success": true, "tableName": "hawaii_cities", "featureCount": 2, "schema": "honua_data", "physicalTableName": "imported_hawaii_cities"}
```

The full result also carries `detectedSrid`, `storedSrid`, `warnings`, and `validationErrors`
(empty when every feature loaded). Step 3 registers a connection to the server's own database, and
Step 4 publishes through that connection.

> **The table you publish is not the name you typed.** The importer stages files under a
> physical `imported_<table>` name, so `tableName` `hawaii_cities` creates
> `honua_data.imported_hawaii_cities`. Read `schema` and `physicalTableName` from this result and
> pass them to Step 4. They are returned so that callers do not have to reconstruct the naming
> convention.

Direct admin uploads execute immediately for an authenticated operator. Agent-driven imports over
`/mcp` require one human approval (`operator.agent.dataset-ingest`): submit `dataset.stage.import`
to file a proposal for human approval in Console Approvals.

Imports create the table in the `honua_data` schema and default `TargetSrid` to 4326.

If you would rather click through a UI, the interactive API explorer at `/docs` is served only when
`HONUA_SERVE_API_DOCS=true` (it defaults on in `Development` and off in `Production`, which is what
the packaged Compose profiles run). The checked-in
[admin OpenAPI document](../developer/api-specs/admin-api.json) is the client-generation contract
either way.

## 3. Register the database

The import writes into the server's own database. On the quickstart stack that database is
`honua`, the user is `honua`, and SSL is off — the same values as `compose.yaml`. Its password
is the one the quickstart generated into `secrets/postgres_password`. The host name
`postgres` is the Compose service, which the server container can resolve. Later steps publish
through that connection. The route accepts the connection id (`connection.connection_id`) or the
connection name; the steps below pass the id.

```bash
python3 -m pip install 'honua-admin==0.3.3' 'honua-sdk==0.3.3'
python3 - <<'PY'
import os

from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

password = open("secrets/postgres_password").read().strip()

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key=os.environ["HONUA_API_KEY"]) as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="hawaii",
        host="postgres",
        port=5432,
        database_name="honua",
        username="honua",
        password=password,
        ssl_mode="Disable",
        ssl_required=False,
    ))
open("connection.id", "w").write(connection.connection_id + "\n")
print(connection.connection_id)
PY
```

## 4. Publish the table

```bash
python3 - <<'PY'
import json
import os

from honua_admin import HonuaAdminClient, PublishLayerRequest

# Step 2 saved the import result; it names the physical table and its schema.
result = json.load(open("import.json"))
connection_id = open("connection.id").read().strip()

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key=os.environ["HONUA_API_KEY"]) as admin:
    layer = admin.publish_layer(connection_id, PublishLayerRequest(
        schema=result.get("schema") or "honua_data",
        # The physical staging table, not the logical name passed to the import.
        table=result.get("physicalTableName") or result["tableName"],
        layer_name="hawaii-cities",
        service_name="default",
        srid=4326,
        # Without this the layer publishes as GeometryCollection and FeatureServer
        # omits geometryType, even though the coordinates come through fine.
        # The geometry column itself is discovered from the table.
        geometry_type="Point",
    ))
if not layer.service_name:
    raise SystemExit("publish did not return a service name")
open("layer.id", "w").write(str(layer.storage_layer_id) + "\n")
open("service.name", "w").write(layer.service_name + "\n")
print(f"{layer.service_name} {layer.storage_layer_id}")
PY
export HONUA_SERVICE="$(cat service.name)"
export HONUA_LAYER_ID="$(cat layer.id)"
```

`$HONUA_SERVICE` and `$HONUA_LAYER_ID` are the service name and numeric layer id from that response.
Later commands on this page, and [the map](first-map.md), read those two variables. The layer is
now available across every enabled protocol.

## 5. Query the published layer

```bash
npx --yes -p @honua/sdk-js@0.5.1 honua services
npx --yes -p @honua/sdk-js@0.5.1 honua layers "$HONUA_SERVICE"
npx --yes -p @honua/sdk-js@0.5.1 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 5
npx --yes -p @honua/sdk-js@0.5.1 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 5 --format geojson
npx --yes -p @honua/sdk-js@0.5.1 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --count
```

The `@honua/sdk-js` pin is the release version, 0.5.1. Omit `HONUA_API_KEY` after the service allows anonymous reads.

The package publishes its CLI as the `honua` binary, so `npx` needs `-p @honua/sdk-js honua`; `npx @honua/sdk-js <command>` looks for a binary named `sdk-js` and fails with `could not determine executable to run`.

**Filtering on imported attributes.** GeoJSON properties land in a `properties` JSONB column rather than as top-level columns, so `--where "population > 100000"` returns HTTP 400 — there is no `population` column to filter. See [query features](../guides/query-analyze/query-features.md) for filtering into the JSON column, or publish with an explicit column mapping if you want first-class attribute columns.

## Verify

The count should be `2`. A one-row GeoJSON check should contain Honolulu or Hilo:

```bash
npx --yes -p @honua/sdk-js@0.5.1 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 1 --format geojson
```

## Troubleshoot

- **401 from an admin operation** — `HONUA_API_KEY` must be the admin password in your install's `secrets/honua_admin_password`; re-run the two `export` lines at the top of this page from the install directory.
- **`Invalid table name`** — pass a `tableName` of letters, digits and underscores that does not start with a digit.
- **`Table 'honua_data.hawaii_cities' was not found`** on publish — publish the physical `imported_hawaii_cities` name, not the logical one you imported under.
- **`could not determine executable to run`** from `npx` — use `-p @honua/sdk-js@0.5.1 honua <command>`.
- **`Master key not configured`** — set `Security__ConnectionEncryption__MasterKey` to a 32-or-more-character value before saving connection credentials.
- **Publishing cannot find the connection** — pass `connection.connection_id` or the name from step 3. The route accepts either.
- **The collection is missing** — confirm the publish result says `enabled=true`, then run `honua services` and `honua layers "$HONUA_SERVICE"` again.

More help: [deployment troubleshooting](../guides/deploy/troubleshooting.md).

## Next steps

- [Make your first map](first-map.md)
- [Publish layers](../guides/publish/publish-layers.md)
- [Query features](../guides/query-analyze/query-features.md)
