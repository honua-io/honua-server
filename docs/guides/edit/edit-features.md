# Edit features

Insert, update, and delete features over HTTP using whichever protocol your client already speaks: GeoServices FeatureServer, OGC API Features, or OData v4.

**Prerequisites:** A running Honua server with a writable PostgreSQL-backed layer (DuckDB, SQL Server, and Oracle providers are read-only). If the layer's access policy restricts writes, add `-H "X-API-Key: $HONUA_ADMIN_PASSWORD"` (or another credential with a write grant — see [Control access](../secure/access-control.md)) to every command below.

All three protocols write to the same store and emit the same change events, so pick one per client and mix freely. The worked example uses FeatureServer; the [equivalence table](#do-the-same-with-ogc-api-features-or-odata) maps each step to the other two protocols.

## Steps

### 1. Insert a feature

```bash
BASE=http://localhost:8080
SERVICE=parks
LAYER=0
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/addFeatures" \
  -H "Content-Type: application/json" \
  -d '{"features":[{"geometry":{"x":-157.8583,"y":21.3069},"attributes":{"name":"Honolulu"}}]}'
```

The response's `addResults[0].objectId` is the new feature's id; note it for the next steps.

### 2. Update the feature

```bash
OBJECTID=1
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/updateFeatures" \
  -H "Content-Type: application/json" \
  -d '{"features":[{"attributes":{"OBJECTID":'$OBJECTID',"name":"Honolulu Hale"}}]}'
```

Updates are matched by `OBJECTID`; attributes you omit are left unchanged, and you may include a `geometry` to move the feature.

### 3. Delete the feature

```bash
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/deleteFeatures" \
  -d "objectIds=$OBJECTID&f=json"
```

### 4. Or batch all three in one call

```bash
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/applyEdits" \
  -d 'adds=[{"geometry":{"x":-157.86,"y":21.31},"attributes":{"name":"Ala Moana"}}]&updates=[]&deletes=&f=json'
```

`applyEdits` accepts `adds`, `updates`, and `deletes` together and also exists at the service level (`/rest/services/$SERVICE/FeatureServer/applyEdits`) for multi-layer edits. Set `rollbackOnFailure=true` to make the batch all-or-nothing.

## Do the same with OGC API Features or OData

| Operation | FeatureServer | OGC API Features | OData v4 |
|---|---|---|---|
| Insert | `POST .../{layerId}/addFeatures` | `POST /ogc/features/collections/{collectionId}/items` (GeoJSON Feature body) | `POST /odata/Layers({layerId})/Features` |
| Replace | `POST .../{layerId}/updateFeatures` | `PUT .../items/{featureId}` | `PUT /odata/Layers({layerId})/Features({objectId})` |
| Partial update | `POST .../{layerId}/updateFeatures` | `PATCH .../items/{featureId}` | `PATCH /odata/Layers({layerId})/Features({objectId})` |
| Delete | `POST .../{layerId}/deleteFeatures` | `DELETE .../items/{featureId}` | `DELETE /odata/Layers({layerId})/Features({objectId})` |
| Batch | `POST .../applyEdits` | `POST .../items/batch` | `POST /odata/$batch` |

OGC API Features takes `Content-Type: application/geo+json` bodies; a successful insert returns `201 Created` with a `Location` header pointing at the new item. OData follows standard v4 semantics, including atomicity groups inside `$batch`. WFS 2.0 `Transaction` (`POST /wfs`) is also supported for XML-based clients.

> SDK: the Honua and ArcGIS Maps SDKs call `applyEdits` for you; point them at `$BASE/rest/services/$SERVICE/FeatureServer`.

## Verify

Query the layer and confirm your edit round-trips:

```bash
curl "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/query?where=name='Honolulu Hale'&outFields=*&f=json"
```

```json
{ "features": [ { "attributes": { "OBJECTID": 1, "name": "Honolulu Hale" }, "geometry": { "x": -157.8583, "y": 21.3069 } } ] }
```

## Troubleshoot

| Symptom | Fix |
|---|---|
| `401` / `403` on a write | The layer or service access policy blocks anonymous writes. Authenticate with an admin API key, a role listed in `allowedWriteRoles`, or a role holding an `insert`/`update`/`delete` grant — see [Control access](../secure/access-control.md). |
| `404` for the service or layer | Check the service name and layer id with `GET $BASE/rest/services?f=json`. The layer must be published and bound to a storage layer. |
| Edits rejected on DuckDB / SQL Server / Oracle layers | Those providers are read-only in the current release. Move writable layers to the PostgreSQL provider. |
| Geometry rejected | Vertex count and payload size are capped by `Limits__Geometry__*`; oversized or invalid geometries return a validation error naming the limit. |
| Partial batch applied | `applyEdits` defaults to per-row results. Pass `rollbackOnFailure=true` for transactional batches. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Add attachments and query related records](attachments-and-related-records.md)
- [React to feature changes](react-to-changes.md)
- [Query features](../query-analyze/query-features.md)
