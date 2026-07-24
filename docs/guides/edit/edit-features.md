# Edit features

Insert, update, and delete features over HTTP using whichever protocol your client already speaks: GeoServices FeatureServer, OGC API Features, or OData v4.

**Prerequisites:** A running Honua server with a writable PostgreSQL-backed layer (DuckDB, SQL Server, and Oracle providers are read-only) and a JavaScript project with `@arcgis/core` installed. If the layer's access policy restricts writes, configure the ArcGIS Maps SDK with a credential carrying a write grant — see [Control access](../secure/access-control.md).

All three protocols write to the same store and emit the same change events, so pick one per client and mix freely. The worked example uses the ArcGIS Maps SDK for JavaScript against FeatureServer; the [equivalence table](#do-the-same-with-ogc-api-features-or-odata) maps each operation to the other two protocols.

## Steps

### 1. Connect to the layer

Create a `FeatureLayer` for the published layer and load its metadata:

```javascript
import Graphic from "@arcgis/core/Graphic.js";
import Point from "@arcgis/core/geometry/Point.js";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer.js";

const layer = new FeatureLayer({
  url: "http://localhost:8080/rest/services/parks/FeatureServer/0",
});
await layer.load();
const objectIdField = layer.objectIdField;
```

### 2. Insert and update a feature

Call the SDK's `applyEdits` method. Keep the returned object id for later edits:

```javascript
const added = await layer.applyEdits({
  addFeatures: [
    new Graphic({
      geometry: new Point({
        longitude: -157.8583,
        latitude: 21.3069,
      }),
      attributes: { name: "Honolulu" },
    }),
  ],
});

const objectId = added.addFeatureResults[0].objectId;

await layer.applyEdits({
  updateFeatures: [
    new Graphic({
      attributes: {
        [objectIdField]: objectId,
        name: "Honolulu Hale",
      },
    }),
  ],
});
```

Attributes you omit are left unchanged, and you may include a `geometry` to move the feature.

### 3. Delete the feature

Pass a graphic carrying the object id to the same SDK method:

```javascript
await layer.applyEdits({
  deleteFeatures: [
    new Graphic({ attributes: { [objectIdField]: objectId } }),
  ],
});
```

`applyEdits` also accepts `addFeatures`, `updateFeatures`, and `deleteFeatures` together. Set `rollbackOnFailureEnabled: true` in its options to make the batch all-or-nothing.

## Do the same with OGC API Features or OData

| Operation | FeatureServer | OGC API Features | OData v4 |
|---|---|---|---|
| Insert | `POST .../{layerId}/addFeatures` | `POST /ogc/features/collections/{collectionId}/items` (GeoJSON Feature body) | `POST /odata/Layers({layerId})/Features` |
| Replace | `POST .../{layerId}/updateFeatures` | `PUT .../items/{featureId}` | `PUT /odata/Layers({layerId})/Features({objectId})` |
| Partial update | `POST .../{layerId}/updateFeatures` | `PATCH .../items/{featureId}` | `PATCH /odata/Layers({layerId})/Features({objectId})` |
| Delete | `POST .../{layerId}/deleteFeatures` | `DELETE .../items/{featureId}` | `DELETE /odata/Layers({layerId})/Features({objectId})` |
| Batch | `POST .../applyEdits` | `POST .../items/batch` | `POST /odata/$batch` |

OGC API Features takes `Content-Type: application/geo+json` bodies; a successful insert returns `201 Created` with a `Location` header pointing at the new item. OData follows standard v4 semantics, including atomicity groups inside `$batch`. WFS 2.0 `Transaction` (`POST /wfs`) is also supported for XML-based clients.

> SDK: the Honua and ArcGIS Maps SDKs call `applyEdits` for you; point them at `/rest/services/{service}/FeatureServer` on the deployment origin.

## Verify

Query the layer and confirm your edit round-trips:

Open `http://localhost:8080/rest/services/parks/FeatureServer/0/query?where=name%3D%27Honolulu%20Hale%27&outFields=*&f=json` in a browser and compare it with the expected response:

```json
{ "features": [ { "attributes": { "OBJECTID": 1, "name": "Honolulu Hale" }, "geometry": { "x": -157.8583, "y": 21.3069 } } ] }
```

## Troubleshoot

| Symptom | Fix |
|---|---|
| `401` / `403` on a write | The layer or service access policy blocks anonymous writes. Authenticate with an admin API key, a role listed in `allowedWriteRoles`, or a role holding an `insert`/`update`/`delete` grant — see [Control access](../secure/access-control.md). |
| `404` for the service or layer | Check the service name and layer id with `honua services` and `honua layers <service-id>`. The layer must be published and bound to a storage layer. |
| Edits rejected on DuckDB / SQL Server / Oracle layers | Those providers are read-only in the current release. Move writable layers to the PostgreSQL provider. |
| Geometry rejected | Vertex count and payload size are capped by `Limits__Geometry__*`; oversized or invalid geometries return a validation error naming the limit. |
| Partial batch applied | `applyEdits` defaults to per-row results. Pass `rollbackOnFailure=true` for transactional batches. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Add attachments and query related records](attachments-and-related-records.md)
- [React to feature changes](react-to-changes.md)
- [Query features](../query-analyze/query-features.md)
