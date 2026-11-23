---
type: guide
title: "Publish 3D scenes"
description: "You'll have a 3D Tiles tileset served from Honua — generated from a PostGIS layer or hosted from existing files — and rendering in CesiumJS in about 15 minutes."
resource: "honua://capability/scene.catalog"
---
# Publish 3D scenes

See the [Honua vocabulary](../../concepts/domain/README.md) for the meanings of Connection, Dataset, Layer, Service, Folder, and Environment.

You'll have a 3D Tiles tileset served from Honua — generated from a PostGIS layer or hosted from existing files — and rendering in CesiumJS in about 15 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)).

Honua serves OGC 3D Tiles through `/scenes/{sceneId}/...` so CesiumJS resolves every nested tile, glTF, and texture URI without URL rewriting. You can generate tilesets from feature layers or host tilesets produced elsewhere.

**Edition and lifecycle:** Existing serving, discovery, elevation, generation from feature layers, and archive and model import remain Community. Specialised CityGML/BIM and point-cloud ingest and I3S remain Enterprise. 3D is Experimental in 2026.1; this guide is for evaluation, with no production commitment. See [commercial boundaries](../../concepts/editions-and-licensing.md#commercial-boundaries-for-20261).

The scene routes are off by default: on a default install they are not mapped and return `404` with an empty body. Before step 1, set both experimental capabilities and restart the server:

- `Capabilities__Experimental__scene.catalog__Enabled=true` for the scene registry (`/api/v1/admin/scenes`) and scene discovery.
- `Capabilities__Experimental__serve.3d-tiles-scene__Enabled=true` for `POST /api/v1/admin/scenes/generate`, archive and model import, and 3D Tiles serving under `/scenes/{sceneId}/...`.

See [enable the 3D surfaces](../../reference/protocols/3d-tiles-and-scenes.md#enable-the-3d-surfaces) for the other scene capabilities.

## Steps

### 1. Generate a tileset from a PostGIS layer

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/scenes/generate` with this body:

```json
{
  "layerId": 7,
  "sceneId": "downtown-buildings",
  "displayName": "Downtown buildings"
}
```

Returns `201 Created` with `tilesetUrl`, `featureCount`, and any `warnings`. The run is synchronous, supports polygon/point/linestring layers in any PostGIS-transformable CRS, auto-registers the output with the scene registry, and forwards the source layer's access policy onto the scene. Optional fields: `description`, `includeAttributes` (attribute allowlist for the tileset metadata), `maxFeatureCount`, `cacheMaxAgeSeconds` (0–86400).

### 2. Extrude flat footprints (optional)

2D layers produce flat geometry unless the layer's catalog metadata declares extrusion. Add an `extrusion` block to the layer metadata:

```json
{
  "extrusion": {
    "heightField": "building_height_m",
    "baseHeightField": "ground_elevation_m",
    "unit": "meters",
    "defaultHeight": 3.0
  }
}
```

`heightField` must be a numeric field; `unit` is `meters` (default), `feet`, or `usSurveyFeet`. The configuration also surfaces to clients as an `extrusionInfo` block on the FeatureServer layer metadata (`GET /rest/services/{serviceId}/FeatureServer/{layerId}`), and the generation pipeline consumes it to produce extruded GLB tiles. Misconfiguration returns `422` with stable codes such as `EXTRUSION_HEIGHT_FIELD_NOT_FOUND`. A later PUT without `extrusion` keeps the stored block; send a new block to change it. FeatureServer omits `extrusionInfo` when the height or base field is hidden.

### 3. Host an existing tileset (alternative to generation)

To upload a tileset or model that is not on the server yet, import it. On a PostgreSQL-backed server, send `multipart/form-data` with a `file` field to:

- `POST /api/v1/admin/scenes/ingest/archive` for a pre-tiled 3D Tiles ZIP or 3TZ archive (up to 256 MiB).
- `POST /api/v1/admin/scenes/ingest/model` for a georeferenced GLB, embedded glTF or OBJ model (up to 128 MiB), placed with the required `longitude` and `latitude` form fields.

Both return `201` with the scene id and tileset URL and register the scene for you. Add an optional `sceneId` and `displayName` to choose the scene's id and name. See [scene import](../../reference/protocols/3d-tiles-and-scenes.md#scene-import-admin-community) for every form field and the archive limits.

Externally produced tilesets that are already on the server's disk register through the scene dataset registry (`POST /api/v1/admin/scenes`), or via configuration for deployments without Postgres:

```json
{
  "Scenes": {
    "Datasets": [
      {
        "Id": "downtown",
        "Name": "Downtown massing model",
        "AssetRoot": "/var/lib/honua/scenes/downtown",
        "TilesetFileName": "tileset.json",
        "AccessPolicy": { "AllowAnonymous": false, "AllowedRoles": ["engineering"] }
      }
    ]
  }
}
```

`AssetRoot` is the directory containing `tileset.json`; omit `AccessPolicy` for a public scene. Republishing is idempotent — overwrite the files and the ETag updates on the next request. Manage registry records with `GET|POST /api/v1/admin/scenes` and `GET|PUT|DELETE /api/v1/admin/scenes/{datasetId}`. The path takes the record's `datasetId` GUID, not its `id` (the scene id); a scene id in the path returns `404`. `DELETE` deactivates the record.

### 4. Render in CesiumJS

```html
<script>
  const viewer = new Cesium.Viewer("cesium");
  const tileset = await Cesium.Cesium3DTileset.fromUrl(
    "http://localhost:8080/scenes/downtown-buildings/tileset.json"
  );
  viewer.scene.primitives.add(tileset);
  await viewer.zoomTo(tileset);
</script>
```

For protected scenes, mint a short-lived token with `POST /scenes/{sceneId}/access-envelope` (any standard Honua auth) and pass it as `queryParameters: { token: envelope.token }` on the root `Cesium.Resource` — Cesium propagates it to every nested asset request.

## Verify

Open `http://localhost:8080/scenes/downtown-buildings/tileset.json` in a browser and compare the first fields with this response:

```json
{"asset":{"version":"1.1","generator":"honua-3dtiles-generator/1.0"},"geometricError":…
```

Nested assets resolve under the same prefix, for example `GET /scenes/downtown-buildings/tile_0000.glb`.

## Scene output storage

The packaged quickstart and production Compose configurations write generated scenes to
`/var/lib/honua/storage/scenes-generated` on the persistent storage volume. For a custom
container deployment, set `SceneGeneration__OutputRoot` to a writable persistent mount
and grant the server user write permission. The default relative directory is unsuitable
for a read-only container filesystem.

`SCENE_OUTPUT_UNWRITABLE` (500) means local scene output could not be written. Check
`SceneGeneration:OutputRoot`, mount permissions and free space before trying again.
The response omits filesystem paths and underlying exception details.

`SCENE_ASSET_STORE_UNWRITABLE` (500) means the object store refused to store the scene assets
for lack of permission. Check that the server's storage credentials (on AWS, the task role) can
write under the configured `FileStorage` bucket and key prefix. Retrying does not help until
access is fixed. On `/api/v1/admin/scenes/generate`, other object-store upload failures are
reported as `503` and can be retried.

## Troubleshoot

- **Bare `404` with an empty body on `/api/v1/admin/scenes`, `/api/v1/admin/scenes/generate` or `/scenes/{sceneId}/tileset.json`** — the experimental scene capabilities are off; set both variables listed above **Steps** and restart the server.
- **`SCENE_FEATURE_LIMIT_EXCEEDED` (400)** — the layer exceeds `SceneGeneration:MaxFeatureCount`; raise the cap or pass a smaller `maxFeatureCount` with a filtered source layer.
- **`SCENE_REGISTRATION_CONFLICT` (409)** — the `sceneId` or display name collides with an existing (active or inactive) registration; pick a new id and name. `DELETE /api/v1/admin/scenes/{datasetId}` only deactivates a record, so it keeps its id and name.
- **Output is flat at Z=0** — the layer has no Z values and no `extrusion` block; configure step 2 and regenerate.
- **`401` on protected scene assets** — the access-envelope token is missing, expired, or bound to another scene; re-issue the envelope and refresh before `expiresAt`.
- **Non-convex polygons render with imperfect fills** — v1 uses fan triangulation; geometry is deterministic but complex rings may need preprocessing.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish terrain and elevation](publish-terrain-and-elevation.md) — terrain under your scenes.
- [Publish layers](publish-layers.md) — publish the source feature layer.
- [Style maps](../style/style-maps.md) — 2D styling for the same layers.
