# 3D Tiles and scenes

Honua hosts registered 3D scene datasets as OGC 3D Tiles (`tileset.json` + assets) for CesiumJS and `<honua-scene>` clients, with an SDK-facing discovery API, an Esri I3S SceneServer adapter, and an admin registry for dataset lifecycle.

## Scene discovery (public, SDK-compatible)

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/scenes` | List scenes visible to the caller. |
| GET | `/api/scenes/{sceneId}` | Scene metadata. |
| GET | `/api/scenes/{sceneId}/resolve` | Serving metadata including the tileset URL. |

## Hosted 3D Tiles serving

| Method | Path | Purpose |
| --- | --- | --- |
| GET, HEAD | `/scenes/{sceneId}/tileset.json` | Root 3D Tiles tileset document. |
| GET, HEAD | `/scenes/{sceneId}/{*assetPath}` | Tile content and sub-tileset assets. |
| POST | `/scenes/{sceneId}/access-envelope` | Issue an access envelope for protected scenes. |
| GET | `/scenes/{sceneId}/exports/openusd/stage.usda` | OpenUSD stage export. |
| GET | `/scenes/{sceneId}/SceneServer` | Esri I3S SceneServer document (Enterprise-gated). |
| GET | `/scenes/{sceneId}/SceneServer/layers/{layerId}` | I3S scene layer (Enterprise-gated). |

`{sceneId}` is the registered URL slug (`[a-z0-9-]{1,64}`). Protected datasets (`requiresAuth`) refuse anonymous access; caching follows the dataset's `cachePolicy` (`maxAgeSeconds` bounded to `[0, 86400]`, or `noStore`).

```bash
# Resolve a scene, then load its tileset
curl "https://server.example.com/api/scenes/downtown/resolve"
curl "https://server.example.com/scenes/downtown/tileset.json"
```

## Scene dataset registry (admin)

All routes require admin authorization and return `application/problem+json` errors.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/v1/admin/scenes` | List active datasets (`?includeInactive=true` for all). |
| POST | `/api/v1/admin/scenes` | Register a dataset; returns `201` with the full record. |
| GET | `/api/v1/admin/scenes/{id}` | Get by `datasetId` (Guid primary key). |
| PUT | `/api/v1/admin/scenes/{id}` | Partial update; omitted/null fields keep current values, `clear*` sentinels clear nullable fields; `revision` increments. |
| DELETE | `/api/v1/admin/scenes/{id}` | Soft-deactivate (status `inactive`); returns `204`. No physical delete. |
| GET | `/api/v1/admin/scenes/{id}/resolve` | Serving metadata plus CesiumJS / `<honua-scene>` embed snippets. |
| POST | `/api/v1/admin/scenes/generate` | Run the 3D Tiles generation pipeline; auto-registers the produced tileset. |

Key record fields: `id` (URL slug), `name` (globally unique), `assetRoot` (server-side directory; traversal and shell metacharacters rejected), `tilesetFileName` (default `tileset.json`), `datasetType` (`hosted_tiles` or `terrain`), `extent` (WGS 84 bbox, all four bounds or none), `crs`, `cachePolicy`, `requiresAuth`/`isPublic` (exactly one true), `allowedRoles`, `status`, `revision`. Full field and validation contract: [scene dataset registry](../../internal/admin-api/scene-dataset-registry.md).

```bash
curl -X POST "https://server.example.com/api/v1/admin/scenes" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"id":"downtown","name":"Downtown massing model","assetRoot":"/data/scenes/downtown","isPublic":true}'
```

## gRPC scene access

`SceneService`, `TileService`, and `ElevationService` expose the same scene/tile/elevation reads over gRPC — see [gRPC](grpc.md).

## Conformance

Hosted tilesets follow the OGC 3D Tiles 1.x content format; the serving routes themselves are Honua surfaces and not CITE-covered. I3S SceneServer is an Esri-compatibility adapter (Enterprise-gated). Standards status: [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md) — generation, extrusion, hosting, and CesiumJS rendering.
- [Publish terrain and elevation](../../guides/publish/publish-terrain-and-elevation.md)
