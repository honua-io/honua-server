# Publish your first dataset

You'll upload a GeoJSON file, publish it as a layer, and query it back over OGC API Features and ArcGIS-compatible FeatureServer in about 10 minutes.

**Prerequisites:** a running server with an admin password set (steps 1–4 of the [quickstart](quickstart.md)), plus `curl`.

## Steps

1. Set your server URL and admin key, and create a GeoJSON file. Admin endpoints authenticate with the `X-API-Key` header.

```bash
export HONUA=http://localhost:8080
export KEY=quickstart-admin-password
cat > cities.geojson <<'EOF'
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Honolulu","population":343421},"geometry":{"type":"Point","coordinates":[-157.8583,21.3069]}},
 {"type":"Feature","properties":{"name":"Hilo","population":44186},"geometry":{"type":"Point","coordinates":[-155.0868,19.7074]}}]}
EOF
```

2. Upload and import the file into PostGIS. `TableName` is required; `TargetSrid` defaults to 4326, and the table is created in the `honua_data` schema.

```bash
curl -s -H "X-API-Key: $KEY" \
  -F "file=@cities.geojson" -F "TableName=hawaii_cities" \
  "$HONUA/api/v1/admin/import/upload"
```

```text
{"success":true,"featureCount":2,"tableName":"hawaii_cities",…}
```

3. Register the database as a named connection (skip if you already created `local` — re-running returns a conflict, which is fine).

```bash
curl -s -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"name":"local","host":"postgres","port":5432,"databaseName":"honua_dev","username":"honua_user","password":"honua_password","sslRequired":false,"sslMode":"Prefer"}' \
  "$HONUA/api/v1/admin/connections"
```

4. Publish the table as a layer on the `default` service. Note the `layerId` and `serviceName` in the response — every protocol serves this layer from now on.

```bash
curl -s -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"schema":"honua_data","table":"hawaii_cities","layerName":"hawaii-cities","srid":4326}' \
  "$HONUA/api/v1/admin/connections/local/layers"
```

```text
{"success":true,"data":{"layerId":2,"layerName":"hawaii-cities","schema":"honua_data","table":"hawaii_cities",…,"serviceName":"default"},…}
```

5. Query it via OGC API Features. List the collections, then set `COLLECTION` to the `id` shown for your layer and fetch items as GeoJSON.

```bash
curl -s -H "X-API-Key: $KEY" "$HONUA/ogc/features/collections"
COLLECTION=hawaii-cities   # use the collection id from the previous response
curl -s -H "X-API-Key: $KEY" "$HONUA/ogc/features/collections/$COLLECTION/items?limit=5"
```

6. Query the same layer via FeatureServer. Fetch the service metadata to see the layer's numeric id, then query it.

```bash
curl -s -H "X-API-Key: $KEY" "$HONUA/rest/services/default/FeatureServer?f=json"
LAYER=0   # use the layer id from the "layers" array in the previous response
curl -s -H "X-API-Key: $KEY" "$HONUA/rest/services/default/FeatureServer/$LAYER/query?where=population%20%3E%20100000&outFields=*&f=json"
```

> Also available with [honua-sdk-js](https://github.com/honua-io/honua-sdk-js) and [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet). Also available in Honua Console — UI guide coming soon.

## Verify

```bash
curl -s -H "X-API-Key: $KEY" "$HONUA/ogc/features/collections/$COLLECTION/items?limit=1"
```

```text
{"type":"FeatureCollection","features":[{"type":"Feature",…,"properties":{"name":"Honolulu","population":343421}}],…}
```

## Troubleshoot

- **401 `Admin authentication not configured`** — `HONUA_ADMIN_PASSWORD` is not set on the container; see the [quickstart](quickstart.md) override file.
- **400 `Table name is required`** — the multipart form must include a `TableName` field alongside `file`.
- **`Master key not configured` on step 3** — set `Security__ConnectionEncryption__MasterKey` (32+ characters) on the container; connection credentials are stored encrypted.
- **404 publishing the layer** — the connection name in the URL does not exist; list connections with `curl -H "X-API-Key: $KEY" "$HONUA/api/v1/admin/connections"`.
- **Collection or layer missing from queries** — check the publish response had `"enabled":true`, and re-list `/ogc/features/collections`; queries without `X-API-Key` return 401 until you allow anonymous reads.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md)

## Next steps

- [Make your first map](first-map.md) — render this layer with vector tiles and MapLibre
- [Publish layers](../guides/publish/publish-layers.md) — fields, geometry types, service options
- [Query features](../guides/query-analyze/query-features.md) — filters, CQL2, and every protocol
