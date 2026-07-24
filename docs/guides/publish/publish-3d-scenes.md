# Publish 3D scenes

You'll have a 3D Tiles tileset served from Honua — generated from a PostGIS layer or hosted from existing files — and rendering in CesiumJS in about 15 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)).

Honua serves OGC 3D Tiles through `/scenes/{sceneId}/...` so CesiumJS resolves every nested tile, glTF, and texture URI without URL rewriting. You can generate tilesets from feature layers or host tilesets produced elsewhere.

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

`heightField` must be a numeric field; `unit` is `meters` (default), `feet`, or `usSurveyFeet`. The configuration also surfaces to clients as an `extrusionInfo` block on the FeatureServer layer metadata (`GET /rest/services/{serviceId}/FeatureServer/{layerId}`), and the generation pipeline consumes it to produce extruded GLB tiles. Misconfiguration returns `422` with stable codes such as `EXTRUSION_HEIGHT_FIELD_NOT_FOUND`.

### 3. Host an existing tileset (alternative to generation)

Externally produced tilesets register through the scene dataset registry (`POST /api/v1/admin/scenes`), or via configuration for deployments without Postgres:

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

`AssetRoot` is the directory containing `tileset.json`; omit `AccessPolicy` for a public scene. Republishing is idempotent — overwrite the files and the ETag updates on the next request. Manage registry records with `GET|POST /api/v1/admin/scenes` and `GET|PUT|DELETE /api/v1/admin/scenes/{id}`.

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

## Troubleshoot

- **`SCENE_FEATURE_LIMIT_EXCEEDED` (400)** — the layer exceeds `SceneGeneration:MaxFeatureCount`; raise the cap or pass a smaller `maxFeatureCount` with a filtered source layer.
- **`SCENE_REGISTRATION_CONFLICT` (409)** — the `sceneId` or display name collides with an existing (active or inactive) registration; pick a new id or delete the old record via `DELETE /api/v1/admin/scenes/{id}`.
- **Output is flat at Z=0** — the layer has no Z values and no `extrusion` block; configure step 2 and regenerate.
- **`401` on protected scene assets** — the access-envelope token is missing, expired, or bound to another scene; re-issue the envelope and refresh before `expiresAt`.
- **Non-convex polygons render with imperfect fills** — v1 uses fan triangulation; geometry is deterministic but complex rings may need preprocessing.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish terrain and elevation](publish-terrain-and-elevation.md) — terrain under your scenes.
- [Publish layers](publish-layers.md) — publish the source feature layer.
- [Style maps](../style/style-maps.md) — 2D styling for the same layers.
