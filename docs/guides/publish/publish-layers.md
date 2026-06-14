# Publish layers

You'll have a PostGIS table published as a live layer — queryable over OGC API Features, FeatureServer, and vector tiles — in about 5 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a PostGIS table to publish (import one with [Import data from files](import-files.md) if needed).

Publishing registers a table in the catalog as a layer; every enabled protocol serves it immediately, no restart required.

> Prefer an SDK? The same endpoints are wrapped by `honua-sdk-js` and `honua-sdk-dotnet`. Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Create a connection

```bash
HONUA_URL=http://localhost:8080
HONUA_API_KEY=your-admin-api-key
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "name": "primary-db",
    "host": "localhost",
    "port": 5432,
    "databaseName": "honua",
    "username": "postgres",
    "password": "secure-password",
    "sslMode": "Require"
  }' \
  "$HONUA_URL/api/v1/admin/connections"
```

Save the returned connection `id`. Skip this step if a connection already exists (`GET /api/v1/admin/connections`).

### 2. List discoverable tables

```bash
CONNECTION_ID=paste-id-from-step-1
curl -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/connections/$CONNECTION_ID/tables"
```

Returns spatial tables with schema, geometry column, geometry type, and SRID. The `{id}` segment accepts the connection GUID or its name.

### 3. Validate the table (optional)

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{"schema":"public","table":"parcels","layerName":"city-parcels"}' \
  "$HONUA_URL/api/v1/admin/connections/$CONNECTION_ID/tables/validate"
```

Validation reports publish-blocking problems (missing primary key, unsupported geometry) before you commit.

### 4. Publish the layer

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "schema": "public",
    "table": "parcels",
    "layerName": "city-parcels",
    "geometryColumn": "geom",
    "srid": 4326
  }' \
  "$HONUA_URL/api/v1/admin/connections/$CONNECTION_ID/layers"
```

Returns `201 Created` with a `layerId` and the owning `serviceName` (defaults to `default`). Optional fields: `description`, `geometryType`, `primaryKey`, `fields` (attribute allowlist), `serviceName`, `enabled`.

### 5. Manage published layers

```bash
curl -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/connections/$CONNECTION_ID/layers"
```

Toggle one layer with `PUT .../layers/{layerId}/enabled`, all layers in a service with `PUT .../layers/enabled` (body `{"enabled": true}`), and refresh catalog extents after bulk data loads with `POST .../layers/extents/refresh`.

## Verify

The layer is live across protocols. OGC API Features:

```bash
curl "$HONUA_URL/ogc/features/collections"
```

```json
{"collections": [{"id": "…", "title": "city-parcels", …}], …}
```

GeoServices FeatureServer (use the `serviceName` and `layerId` from step 4):

```bash
LAYER_ID=paste-layerid-from-step-4
curl "$HONUA_URL/rest/services/default/FeatureServer/$LAYER_ID?f=json"
```

Vector tiles:

```bash
curl "$HONUA_URL/tiles/$LAYER_ID/tile.json"
```

```json
{"tilejson": "3.0.0", "tiles": ["…/tiles/1/{z}/{x}/{y}.mvt"], …}
```

## Troubleshoot

- **`Validation failed: …` (400)** — the request is missing required fields; `schema`, `table`, and `layerName` are mandatory.
- **`409 Conflict` on publish** — a layer with the same name already exists in the target service; change `layerName` or `serviceName`.
- **`404` on publish** — the connection id or the schema/table does not exist; re-check step 2 output.
- **Layer published but absent from `/ogc/features/collections`** — confirm the layer is enabled (`GET .../layers`) and the protocol is enabled for the service (`GET /api/v1/admin/services/{serviceName}/settings`).
- **Stale or empty extent on the map** — run `POST .../layers/extents/refresh` after loading data into the source table.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Style maps](../style/style-maps.md) — attach a MapLibre style to the layer.
- [Publish tiles](publish-tiles.md) — seed and manage the vector tile cache.
- [Query features](../query-analyze/query-features.md) — query the published layer.
