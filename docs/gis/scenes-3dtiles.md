# Hosted 3D Tiles Scenes

Honua serves already-hosted [OGC 3D Tiles](https://www.ogc.org/standard/3dtiles/)
tilesets through dedicated scene endpoints so [CesiumJS](https://cesium.com/platform/cesiumjs/)
and other standards-based clients can load `tileset.json` and resolve all
relative tile, glTF, texture, and binary asset URIs without any client-side URL
rewriting.

This is the foundation slice of Honua's 3D support. It covers serving;
generating 3D Tiles from PostGIS, raster, or model sources is a separate
deliverable.

## Public routes

| Route | Method | Purpose |
| --- | --- | --- |
| `/scenes/{sceneId}/tileset.json` | GET, HEAD | Root 3D Tiles document (`application/json`). |
| `/scenes/{sceneId}/{*assetPath}` | GET, HEAD | Tile, glTF, texture, JSON, or binary payload under the scene's asset prefix. |

`sceneId` is operator-defined and stable; clients hard-code it the same way
they do for layer ids on other Honua endpoints. CesiumJS resolves nested
`uri` references inside `tileset.json` relative to the root URL, so
`./tiles/0/0/0.b3dm` inside the document maps directly to
`/scenes/{sceneId}/tiles/0/0/0.b3dm`.

## Configuration

Scenes are registered in the `Scenes` configuration section. The initial
implementation is configuration-driven; a database-backed registry will replace
this surface in a follow-up issue without changing the URL contract.

```json
{
  "Scenes": {
    "Datasets": [
      {
        "Id": "downtown",
        "Name": "Downtown massing model",
        "Description": "Photogrammetry tileset, 2026 Q1.",
        "AssetRoot": "/var/lib/honua/scenes/downtown",
        "TilesetFileName": "tileset.json"
      },
      {
        "Id": "internal-bridge",
        "Name": "Bridge inspection scan",
        "AssetRoot": "/var/lib/honua/scenes/bridge-2026",
        "AccessPolicy": {
          "AllowAnonymous": false,
          "AllowedRoles": ["fieldops", "engineering"]
        }
      }
    ]
  }
}
```

| Property | Required | Description |
| --- | --- | --- |
| `Id` | yes | Stable identifier surfaced in the URL. |
| `Name` | no | Human-readable label; defaults to `Id` when omitted. |
| `Description` | no | Optional description for tooling/UI surfaces. |
| `AssetRoot` | yes | Filesystem directory containing `tileset.json`. Absolute, or relative to the application content root. |
| `TilesetFileName` | no | Defaults to `tileset.json`. Set when the publisher uses a non-standard root filename. |
| `AccessPolicy` | no | Reuses the catalog `AccessPolicy` shape (`AllowAnonymous`, `AllowedRoles`, etc.). When omitted, the scene is public. |

Honua canonicalizes `AssetRoot` once at startup. Hosted scenes are idempotent
to republish: overwriting the underlying files updates the deterministic ETag
on the next request.

## CesiumJS usage

```html
<script src="https://cesium.com/downloads/cesiumjs/releases/1.116/Build/Cesium/Cesium.js"></script>
<link href="https://cesium.com/downloads/cesiumjs/releases/1.116/Build/Cesium/Widgets/widgets.css" rel="stylesheet">

<div id="cesium" style="width:100%;height:100vh"></div>

<script>
  const viewer = new Cesium.Viewer("cesium");
  const tileset = await Cesium.Cesium3DTileset.fromUrl(
    // Use the base URL of your Honua deployment.
    "https://honua.example.com/scenes/downtown/tileset.json"
  );
  viewer.scene.primitives.add(tileset);
  await viewer.zoomTo(tileset);
</script>
```

No URL rewriting hooks are required. CesiumJS' internal resource loader
resolves every nested `tileset.json`, `b3dm`, `glb`, and texture URI under
`/scenes/{sceneId}/`.

### MIME types

Honua emits the canonical media types Cesium expects:

| Extension | `Content-Type` |
| --- | --- |
| `.json` | `application/json` |
| `.b3dm`, `.i3dm`, `.pnts`, `.cmpt`, `.bin` | `application/octet-stream` |
| `.glb` | `model/gltf-binary` |
| `.gltf` | `model/gltf+json` |
| `.png`, `.jpg`, `.jpeg`, `.webp`, `.ktx`, `.ktx2`, `.basis` | matching `image/*` type |
| Unknown | `application/octet-stream` |

## Caching, ETags, and conditional requests

Both routes set `ETag`, `Last-Modified`, and `Accept-Ranges: bytes`. The
`Cache-Control` value depends on the scene's access policy:

- **Public scenes** (no `AccessPolicy`): `Cache-Control: public, max-age=...`
  so CDNs and shared proxies can store and re-serve the payload.
- **Protected scenes** (any `AccessPolicy`): `Cache-Control: private, max-age=...`
  plus `Vary: Authorization`. Shared caches must not store the body — every
  request needs to re-run the dataset access policy — but a user agent's
  private cache may still revalidate within `max-age`.

The default TTLs are:

| Policy | Default | Override |
| --- | --- | --- |
| `tileset.json` metadata | 10 minutes | `OutputCache:SceneTilesetMetadata` |
| Tile / binary / texture asset | 1 hour | `OutputCache:SceneTileAsset` |

ETags are deterministic per file — formatted as quoted
`"<lastWriteUtcTicks-hex>-<lengthBytes-hex>"` — so cached responses survive
process restarts as long as the underlying volume preserves file metadata.
Clients that send `If-None-Match` (including the `W/` weak prefix) will
receive `304 Not Modified` when the file is unchanged. `HEAD` is supported
for cheap freshness probes; range requests are advertised through
`Accept-Ranges: bytes` and honoured by ASP.NET Core's static-file pipeline.
Requests that include a `Range` header bypass the output cache so the
static-file pipeline can return `206 Partial Content` with `Content-Range`
directly; full GETs still hit the shared cache.

## Authorization

Each request runs through the existing dataset access policy:

- Public scenes (no `AccessPolicy`) accept anonymous reads.
- Protected scenes apply the catalog `AccessPolicy` (`AllowAnonymous`,
  `AllowedRoles`) on **every** request — root tileset, nested tilesets, and
  every binary asset.

> **Browser caveat.** CesiumJS' internal resource loader cannot attach
> custom `Authorization` headers or session cookies to nested tile fetches.
> For *non-browser* clients (server-to-server, mobile/native) the existing
> token/session auth works end-to-end. Browser-safe protected rendering —
> signed-URL or proxy handoff — is delivered separately by
> [honua-server-849](https://github.com/honua-io/honua-server/issues/849).
> Until then, do not rely on the auth check alone to gate sensitive scenes
> rendered in browsers.

## CORS

The scene routes inherit Honua's shared CORS policy. The
`Access-Control-Expose-Headers` list already includes `ETag`,
`Accept-Ranges`, `Content-Length`, and `Content-Range`, which are exactly
the headers Cesium's resource loader needs for caching and range-aware
streaming. No per-route CORS configuration is required.

## Path safety

The asset resolver canonicalizes every request before file I/O and rejects:

- Empty paths, leading `/` or `\`, drive-letter prefixes, and UNC paths.
- Any segment equal to `.` or `..`, embedded `\`, or null bytes.
- Percent-encoded variants of the above (`%2e`, `%2f`, `%5c`).
- Any canonical path that does not begin with the scene's `AssetRoot`.
- Files or intermediate directories under `AssetRoot` that are symbolic
  links / reparse points. The lexical prefix check above is necessary but
  not sufficient — a link could otherwise redirect file I/O to a target
  outside the root while the request URL still appears to be under it.

Path-traversal probes return `400 Bad Request` rather than `404` to avoid
fingerprinting the underlying layout. Missing files return `404 Not Found`.

External absolute `uri` values inside a hosted `tileset.json` are followed
directly by the client (and never proxied through Honua), which keeps the
server out of the path for third-party tile sources referenced by the
publisher.

## CesiumJS browser smoke

A Playwright smoke spec at
[`tests/js-browser/cesium/3d-tiles-scene.spec.ts`](../../tests/js-browser/cesium/3d-tiles-scene.spec.ts)
exercises the public route contract end-to-end against a real CesiumJS build
in headless Chromium. It is the merge-blocking signal for this surface:

- `cesium-3d-tiles-smoke` job in `ci.yml` runs on every integration-bearing PR.
- Full nightly Cesium lane (`client-interop-nightly.yml` via
  `docker/client-compat/cesium/`) reruns the same spec and emits a
  `js-cesium-3d-tiles.cert.json` envelope.

Asserted contracts: `tileset.json` returns `application/json` with
`Access-Control-Allow-Origin` echoing an explicitly allowlisted origin (or
`*`); the first nested tile content URI resolves with the canonical media
type for its extension and an `Access-Control-Allow-Origin` value matching
the request origin (or `*`); and `Cesium3DTileset.fromUrl` loads, fetches
at least one binary tile body (`.b3dm` / `.glb`-shaped URL with 2xx),
returns no 4xx/5xx for any `/scenes/**` request, surfaces no Playwright
network failures, and emits no Cesium `tileFailed` events. The 4xx-or-5xx
gate plus the binary-tile-fetch assertion ensure a missing or broken
nested-asset route fails the smoke (a previous 2xx-count check could be
satisfied by `tileset.json` alone). When `CI=true` is set, the spec
fails-fast on a 404 from `tileset.json` instead of skipping, so missing
fixture binding or route regressions surface as a hard gate failure;
local ad-hoc runs without the fixture bound get a helpful skip with a
configuration hint. Browser-protected scenes (`AccessPolicy` set) are
recorded as a deferred `CERT-AUTH-01 — DEFERRED to honua-server-849`
skip until the signed-handoff slice in `honua-server-849` lands.

See [`tests/js-browser/cesium/README.md`](../../tests/js-browser/cesium/README.md)
for the local command, required `Scenes:Datasets__*` / `Cors:AllowedOrigins`
configuration, and the smoke vs. full-lane split.
