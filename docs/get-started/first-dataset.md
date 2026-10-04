---
type: guide
title: "Publish your first dataset"
description: "Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients."
---
# Publish your first dataset

Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients.

**Prerequisites:** a running server with an admin password set (steps 1–4 of the [quickstart](quickstart.md)), Python 3.11+ with the pinned clients `honua-admin` 0.1.9 and `honua-sdk` 0.1.12 (step 2 installs them; `httpx` comes in with `honua-sdk`), and Node.js with `npx`.

> **Shell.** Every block on this page is `bash` — heredocs, `export`, and `python3`. On Windows run
> them in WSL or Git Bash, not PowerShell, and note that a bare `python3` there resolves to the
> Microsoft Store stub; use `python` or a venv interpreter. The [quickstart](quickstart.md) shows
> the PowerShell equivalents where they differ.

> **Base URL and admin key.** Steps 1–4 of the [quickstart](quickstart.md) write a generated
> password into `.env` and publish the server on `18080`. Take both from there rather than
> retyping the literals below:
>
> ```bash
> cd <your quickstart install directory>
> export HONUA_BASE_URL="http://localhost:$(grep '^HONUA_HTTP_PORT=' .env | cut -d= -f2)"
> export HONUA_API_KEY="$(grep '^HONUA_ADMIN_PASSWORD=' .env | cut -d= -f2)"
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

The file-upload operation does not yet have a high-level SDK wrapper
([honua-sdk-python#267](https://github.com/honua-io/honua-sdk-python/issues/267)). Install the pinned clients
(`httpx` is a dependency of `honua-sdk`), then call the endpoint. The admin API authenticates with
the `X-API-Key` header. HTTP Basic auth is refused.

```bash
python3 -m pip install 'honua-admin==0.1.9' 'honua-sdk==0.1.12'
```

<!-- doc-run: blocked https://github.com/honua-io/honua-sdk-python/issues/267 -->

```bash
python3 - <<'PY'
import json
import os

import httpx

with httpx.Client() as client, open("cities.geojson", "rb") as fh:
    response = client.post(
        f"{os.environ['HONUA_BASE_URL']}/api/v1/admin/import/upload",
        headers={"X-API-Key": os.environ["HONUA_API_KEY"]},
        files={"file": fh},
        data={"TableName": "hawaii_cities"},
    )
response.raise_for_status()
result = response.json()
json.dump(result, open("import.json", "w"))
stable = {
    key: result[key]
    for key in (
        "success",
        "featureCount",
        "tableName",
        "physicalTableName",
        "schema",
        "detectedSrid",
    )
}
print(json.dumps(stable))
PY
```

A successful import responds with:

```json
{"success": true, "featureCount": 2, "tableName": "hawaii_cities", "physicalTableName": "imported_hawaii_cities", "schema": "honua_data", "detectedSrid": 4326}
```

The full response also includes `sourceKind`, a numeric `format` code, and a `duration` that
changes from run to run. The six fields above are the ones a caller can rely on.

> **The table you publish is not the name you typed.** The importer stages files under a
> physical `imported_<table>` name, so `TableName=hawaii_cities` creates
> `honua_data.imported_hawaii_cities`. Read `physicalTableName` and `schema` from this
> response and pass them to Step 4 — they are returned precisely so callers do not have to
> reconstruct the naming convention.

Imports create the table in the `honua_data` schema and default `TargetSrid` to 4326.

If you would rather click through a UI, the interactive API explorer at `/docs` is served only when
`HONUA_SERVE_API_DOCS=true` (it defaults on in `Development` and off in `Production`, which is what
the packaged Compose profiles run). The checked-in
[admin OpenAPI document](../developer/api-specs/admin-api.json) is the client-generation contract
either way.

## 3. Register the database

The import writes into the server's own database. On the quickstart stack that database is
`honua`, the user is `honua`, and SSL is off — the same values as `compose.yaml`. The host name
`postgres` is the Compose service, which the server container can resolve. Later steps publish
through that connection. The route accepts the connection id (`connection.connection_id`) or the
connection name; the steps below pass the id.

```bash
python3 -m pip install 'honua-admin==0.1.9' 'honua-sdk==0.1.12'
python3 - <<'PY'
import os
import subprocess

from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

password = subprocess.check_output(
    ["docker", "compose", "exec", "-T", "postgres", "printenv", "POSTGRES_PASSWORD"],
    text=True,
).strip()

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

# Step 2 saved the import response; it names the physical table and its schema.
result = json.load(open("import.json"))
connection_id = open("connection.id").read().strip()

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key=os.environ["HONUA_API_KEY"]) as admin:
    layer = admin.publish_layer(connection_id, PublishLayerRequest(
        schema=result["schema"],
        # The physical staging table, not the logical name passed to the import.
        table=result["physicalTableName"],
        layer_name="hawaii-cities",
        service_name="default",
        srid=4326,
        # Without this the layer publishes as GeometryCollection and FeatureServer
        # reports esriGeometryNull, even though the coordinates come through fine.
        # The geometry column itself is discovered from the table.
        geometry_type="Point",
    ))
if not layer.service_name:
    raise SystemExit("publish did not return a service name")
open("layer.id", "w").write(str(layer.layer_id) + "\n")
open("service.name", "w").write(layer.service_name + "\n")
print(f"{layer.service_name} {layer.layer_id}")
PY
export HONUA_SERVICE="$(cat service.name)"
export HONUA_LAYER_ID="$(cat layer.id)"
```

`$HONUA_SERVICE` and `$HONUA_LAYER_ID` are the service name and numeric layer id from that response.
Later commands on this page, and [the map](first-map.md), read those two variables. The layer is
now available across every enabled protocol.

## 5. Query the published layer

```bash
npx --yes -p @honua/sdk-js@0.1.12 honua services
npx --yes -p @honua/sdk-js@0.1.12 honua layers "$HONUA_SERVICE"
npx --yes -p @honua/sdk-js@0.1.12 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 5
npx --yes -p @honua/sdk-js@0.1.12 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 5 --format geojson
npx --yes -p @honua/sdk-js@0.1.12 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --count
```

The `@honua/sdk-js` pin is the release version, 0.1.12. Omit `HONUA_API_KEY` after the service allows anonymous reads.

The package publishes its CLI as the `honua` binary, so `npx` needs `-p @honua/sdk-js honua`; `npx @honua/sdk-js <command>` looks for a binary named `sdk-js` and fails with `could not determine executable to run`.

**Filtering on imported attributes.** GeoJSON properties land in a `properties` JSONB column rather than as top-level columns, so `--where "population > 100000"` returns HTTP 400 — there is no `population` column to filter. See [query features](../guides/query-analyze/query-features.md) for filtering into the JSON column, or publish with an explicit column mapping if you want first-class attribute columns.

## Verify

The count should be `2`. A one-row GeoJSON check should contain Honolulu or Hilo:

```bash
npx --yes -p @honua/sdk-js@0.1.12 honua query "$HONUA_SERVICE/$HONUA_LAYER_ID" --limit 1 --format geojson
```

## Troubleshoot

- **401 from an admin operation** — `HONUA_API_KEY` must be the `HONUA_ADMIN_PASSWORD` from your install's `.env`; re-run the two `export` lines at the top of this page.
- **`Table name is required`** — pass `TableName` in the `data` field alongside `file` on the import call.
- **`Table 'honua_data.hawaii_cities' was not found`** on publish — publish the physical `imported_hawaii_cities` name, not the logical one you imported under.
- **`could not determine executable to run`** from `npx` — use `-p @honua/sdk-js@0.1.12 honua <command>`.
- **`Master key not configured`** — set `Security__ConnectionEncryption__MasterKey` to a 32-or-more-character value before saving connection credentials.
- **Publishing cannot find the connection** — pass `connection.connection_id` or the name from step 3. The route accepts either.
- **The collection is missing** — confirm the publish result says `enabled=true`, then run `honua services` and `honua layers "$HONUA_SERVICE"` again.

More help: [deployment troubleshooting](../guides/deploy/troubleshooting.md).

## Next steps

- [Make your first map](first-map.md)
- [Publish layers](../guides/publish/publish-layers.md)
- [Query features](../guides/query-analyze/query-features.md)
