# Publish your first dataset

Upload a GeoJSON file, publish it as a layer, and query it through the supported Honua clients.

**Prerequisites:** a running server with an admin password set (steps 1–4 of the [quickstart](quickstart.md)), Python with `honua-admin`, and the `honua` CLI (`npm install --global @honua/sdk-js`).

## 1. Create a small dataset

```bash
cat > cities.geojson <<'EOF'
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Honolulu","population":343421},"geometry":{"type":"Point","coordinates":[-157.8583,21.3069]}},
 {"type":"Feature","properties":{"name":"Hilo","population":44186},"geometry":{"type":"Point","coordinates":[-155.0868,19.7074]}}]}
EOF
```

## 2. Import the file

The file-upload operation does not yet have a high-level SDK wrapper. Open the local [API explorer](http://localhost:8080/docs), choose `POST /api/v1/admin/import/upload`, authorize with `quickstart-admin-password`, attach `cities.geojson`, set `TableName` to `hawaii_cities`, and execute it.

The checked-in [admin OpenAPI document](../developer/api-specs/admin-api.json) is the client-generation contract when the explorer is unavailable. Imports create the table in the `honua_data` schema and default `TargetSrid` to 4326.

## 3. Register the database

Install the control-plane SDK and create the named connection. Skip the create call if `local` already exists.

```bash
python3 -m pip install \
  "honua-sdk @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-sdk" \
  "honua-admin @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-admin"
python3 - <<'PY'
from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

with HonuaAdminClient("http://localhost:8080", api_key="quickstart-admin-password") as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="local",
        host="postgres",
        port=5432,
        database_name="honua_dev",
        username="honua_user",
        password="honua_password",
        ssl_mode="Prefer",
        ssl_required=False,
    ))
    print(connection)
PY
```

## 4. Publish the table

```bash
python3 - <<'PY'
from honua_admin import HonuaAdminClient, PublishLayerRequest

with HonuaAdminClient("http://localhost:8080", api_key="quickstart-admin-password") as admin:
    layer = admin.publish_layer("local", PublishLayerRequest(
        schema="honua_data",
        table="hawaii_cities",
        layer_name="hawaii-cities",
        srid=4326,
    ))
    print(layer)
PY
```

Record the returned `layerId` and `serviceName`. The layer is now available across every enabled protocol.

## 5. Query the published layer

```bash
export HONUA_BASE_URL=http://localhost:8080
export HONUA_API_KEY=quickstart-admin-password

honua services
honua layers default
honua query default/0 --limit 5
honua query default/0 --where "population > 100000" --format geojson
honua query default/0 --count
```

Use the actual numeric layer ID printed by the publish step. Omit `HONUA_API_KEY` after the service allows anonymous reads.

## Verify

The count should be `2`. A one-row GeoJSON check should contain Honolulu or Hilo:

```bash
honua query default/0 --limit 1 --format geojson
```

## Troubleshoot

- **401 from an admin operation** — set `HONUA_ADMIN_PASSWORD` on the server; the repository Compose profile uses the development value shown above.
- **`Table name is required`** — set `TableName` alongside the file in the explorer.
- **`Master key not configured`** — set `Security__ConnectionEncryption__MasterKey` to a 32-or-more-character value before saving connection credentials.
- **Publishing cannot find the connection** — inspect `HonuaAdminClient.list_connections()` and pass the returned connection ID or name.
- **The collection is missing** — confirm the publish result says `enabled=true`, then run `honua services` and `honua layers default` again.

More help: [deployment troubleshooting](../guides/deploy/troubleshooting.md).

## Next steps

- [Make your first map](first-map.md)
- [Publish layers](../guides/publish/publish-layers.md)
- [Query features](../guides/query-analyze/query-features.md)
