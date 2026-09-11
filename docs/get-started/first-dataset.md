---
type: guide
title: "Publish your first dataset"
description: "Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients."
---
# Publish your first dataset

Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients.

**Prerequisites:** a running server with an admin password set (steps 1–4 of the [quickstart](quickstart.md)), Python with `honua-admin`, and Node.js with `npx`.

> **Shell.** Every block on this page is `bash` — heredocs, `export`, and `python3`. On Windows run > them in WSL or Git Bash, not PowerShell, and note that a bare `python3` there resolves to the > Microsoft Store stub; use `python` or a venv interpreter. The [quickstart](quickstart.md) is the > PowerShell-native path.

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

The file-upload operation does not yet have a high-level SDK wrapper, so call the endpoint directly:

```bash
curl -H "X-API-Key: $HONUA_API_KEY" \
  -F file=@cities.geojson \
  -F TableName=hawaii_cities \
  "$HONUA_BASE_URL/api/v1/admin/import/upload" | tee import.json
```

The admin API authenticates with the `X-API-Key` header. HTTP Basic (`curl -u`) is refused:
`API key required. Provide a valid API key in the X-API-Key header.`

A successful import responds with:

```json
{"success":true,"featureCount":2,"tableName":"hawaii_cities",
 "physicalTableName":"imported_hawaii_cities","schema":"honua_data",
 "sourceKind":"file","format":"GeoJson","detectedSrid":4326}
```

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

Install the control-plane SDK and create the named connection. Skip the create call if `local` already exists.

```bash
python3 -m pip install \
  "honua-sdk @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-sdk" \
  "honua-admin @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-admin"
python3 - <<'PY'
import subprocess

import os

from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key=os.environ["HONUA_API_KEY"]) as admin:
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
    print(connection)
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

with HonuaAdminClient(os.environ["HONUA_BASE_URL"], api_key=os.environ["HONUA_API_KEY"]) as admin:
    layer = admin.publish_layer("local", PublishLayerRequest(
        schema=result["schema"],
        # The physical staging table, not the logical name passed to the import.
        table=result["physicalTableName"],
        layer_name="hawaii-cities",
        srid=4326,
        # Without this the layer publishes as GeometryCollection and FeatureServer
        # reports esriGeometryNull, even though the coordinates come through fine.
        # The geometry column itself is discovered from the table.
        geometry_type="Point",
    ))
    print(layer)
PY
```

Record the returned `layerId` and `serviceName`. The layer is now available across every enabled protocol.

## 5. Query the published layer

```bash
npx --yes -p @honua/sdk-js honua services
npx --yes -p @honua/sdk-js honua layers default
npx --yes -p @honua/sdk-js honua query default/0 --limit 5
npx --yes -p @honua/sdk-js honua query default/0 --limit 5 --format geojson
npx --yes -p @honua/sdk-js honua query default/0 --count
```

Use the actual numeric layer ID printed by the publish step. Omit `HONUA_API_KEY` after the service allows anonymous reads.

The package publishes its CLI as the `honua` binary, so `npx` needs `-p @honua/sdk-js honua`; `npx @honua/sdk-js <command>` looks for a binary named `sdk-js` and fails with `could not determine executable to run`.

**Filtering on imported attributes.** GeoJSON properties land in a `properties` JSONB column rather than as top-level columns, so `--where "population > 100000"` returns HTTP 400 — there is no `population` column to filter. See [query features](../guides/query-analyze/query-features.md) for filtering into the JSON column, or publish with an explicit column mapping if you want first-class attribute columns.

## Verify

The count should be `2`. A one-row GeoJSON check should contain Honolulu or Hilo:

```bash
npx --yes -p @honua/sdk-js honua query default/0 --limit 1 --format geojson
```

## Troubleshoot

- **401 from an admin operation** — set `HONUA_ADMIN_PASSWORD` on the server; the repository Compose profile uses the development value shown above.
- **`Table name is required`** — pass `-F TableName=...` alongside `-F file=@...` on the import call.
- **`Table 'honua_data.hawaii_cities' was not found`** on publish — publish the physical `imported_hawaii_cities` name, not the logical one you imported under.
- **`could not determine executable to run`** from `npx` — use `-p @honua/sdk-js honua <command>`.
- **`Master key not configured`** — set `Security__ConnectionEncryption__MasterKey` to a 32-or-more-character value before saving connection credentials.
- **Publishing cannot find the connection** — inspect `HonuaAdminClient.list_connections()` and pass the returned connection ID or name.
- **The collection is missing** — confirm the publish result says `enabled=true`, then run `honua services` and `honua layers default` again.

More help: [deployment troubleshooting](../guides/deploy/troubleshooting.md).

## Next steps

- [Make your first map](first-map.md)
- [Publish layers](../guides/publish/publish-layers.md)
- [Query features](../guides/query-analyze/query-features.md)
