---
type: guide
title: "Edit features"
description: "Insert, update, and delete features over HTTP using whichever protocol your client already speaks: GeoServices FeatureServer, OGC API Features, or OData v4."
resource: "honua://capability/editing.featureserver-edits"
---
# Edit features

Insert, update, and delete features over HTTP using whichever protocol your client already speaks: GeoServices FeatureServer, OGC API Features, or OData v4.

**Prerequisites:** A running Honua server with an editable PostgreSQL-backed layer (DuckDB, SQL Server, and Oracle providers are read-only) and a JavaScript project with `@arcgis/core` installed. If the layer's access policy restricts writes, configure the ArcGIS Maps SDK with a credential carrying a write grant — see [Control access](../secure/access-control.md).

A layer published with the default settings is read-only (its FeatureServer metadata lists only `Query`). To accept edits, publish it into the managed feature store with `"storageMode": "managed"` and the edit capabilities, for example `"capabilities": ["Query", "Create", "Update", "Delete"]`; see [editable layers](../../reference/admin-api/connections-and-layers.md#editable-layers). The layer metadata's `capabilities` then lists `Create`, `Update` and `Delete`, and the managed store assigns the object ids.

All three protocols write to the same store and emit the same change events, so pick one per client and mix freely. The worked example uses the ArcGIS Maps SDK for JavaScript, which Honua works with, against FeatureServer; the [equivalence table](#do-the-same-with-ogc-api-features-or-odata) maps each operation to the other two protocols.

## Steps

### 1. Connect to the layer

Create a `FeatureLayer` for the published layer and load its metadata:

```javascript
import Graphic from "@arcgis/core/Graphic.js";
import Point from "@arcgis/core/geometry/Point.js";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer.js";

const layer = new FeatureLayer({
  url: "http://localhost:8080/rest/services/parks/FeatureServer/1", // a layer id from the service's layers list
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

`applyEdits` also accepts `addFeatures`, `updateFeatures`, and `deleteFeatures` together. The server applies a batch all-or-nothing unless the request says `rollbackOnFailure=false`; set `rollbackOnFailureEnabled` in the SDK options to state which you want.

## Do the same with OGC API Features or OData

| Operation | FeatureServer | OGC API Features | OData v4 |
|---|---|---|---|
| Insert | `POST .../{layerId}/addFeatures` | `POST /ogc/features/collections/{collectionId}/items` (GeoJSON Feature body) | `POST /odata/Layers({layerId})/Features` |
| Replace | `POST .../{layerId}/updateFeatures` with every attribute | `PUT .../items/{featureId}` | `PUT /odata/Layers({layerId})/Features({objectId})` |
| Partial update | `POST .../{layerId}/updateFeatures` | `PATCH .../items/{featureId}` | `PATCH /odata/Layers({layerId})/Features({objectId})` |
| Delete | `POST .../{layerId}/deleteFeatures` | `DELETE .../items/{featureId}` | `DELETE /odata/Layers({layerId})/Features({objectId})` |
| Batch | `POST .../applyEdits` | `POST .../items/batch` | `POST /odata/$batch` |

OGC API Features takes `Content-Type: application/geo+json` bodies for `POST` and `PUT`, and `application/merge-patch+json` for `PATCH` (another type returns `415`); a successful insert returns `201 Created` with a `Location` header pointing at the new item. `PUT` replaces the feature: in OGC API Features and OData an attribute you leave out becomes null, and an OData `PUT` without `Geometry` clears the geometry. `updateFeatures` and `PATCH` leave omitted attributes unchanged. OData follows standard v4 semantics, including atomicity groups inside `$batch`. WFS 2.0 `Transaction` (`POST /wfs`) is also supported for XML-based clients.

> SDK: the Honua and ArcGIS Maps SDKs call `applyEdits` for you; point them at `/rest/services/{service}/FeatureServer` on the deployment origin.

## Verify

Query the layer and confirm your edit round-trips:

Open `http://localhost:8080/rest/services/parks/FeatureServer/1/query?where=name%3D%27Honolulu%20Hale%27&outFields=*&f=json` in a browser and compare it with the expected response (the object id is the one `addFeatures` returned):

```json
{ "features": [ { "attributes": { "objectid": 70052, "name": "Honolulu Hale" }, "geometry": { "x": -157.8583, "y": 21.3069 } } ] }
```

## Troubleshoot

| Symptom | Fix |
|---|---|
| `401` / `403` on a write (FeatureServer: HTTP 200 with error code `499` when the credential is missing) | The layer or service access policy blocks anonymous writes. Authenticate with an admin API key, a role listed in `allowedWriteRoles`, or a role holding an `insert`/`update`/`delete` grant — see [Control access](../secure/access-control.md). |
| `Create edits are not enabled` (also `Update`/`Delete`): FeatureServer error 400, OGC API Features and OData `405` | The layer was published without edit capabilities. Publish a managed copy with `storageMode` `managed` and the edit capabilities (it gets new object ids); see [editable layers](../../reference/admin-api/connections-and-layers.md#editable-layers). |
| `404` for the service or layer (FeatureServer: HTTP 200 with error code `404`) | Check the service name and layer id with `honua services` and `honua layers <service-id>`. The layer must be published and bound to a storage layer. |
| Edits rejected on DuckDB / SQL Server / Oracle layers | Those providers are read-only in the current release. Move writable layers to the PostgreSQL provider. |
| Geometry rejected | Vertex count and payload size are capped by `Limits__Geometry__*`; oversized or invalid geometries return a validation error naming the limit. |
| A valid row in a batch was not written | `applyEdits` rolls the whole batch back when one row fails (the other rows report code `1008`). Pass `rollbackOnFailure=false` to keep the rows that succeed. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Add attachments and query related records](attachments-and-related-records.md)
- [React to feature changes](react-to-changes.md)
- [Query features](../query-analyze/query-features.md)

Esri, ArcGIS, and the Esri product names used here are trademarks, registered trademarks, or service
marks of Esri in the United States and other countries. They identify the products Honua is compatible
with; Honua is not affiliated with, sponsored by, or endorsed by Esri.
