# Hosted 3D Tiles Scenes

Honua serves already-hosted [OGC 3D Tiles](https://www.ogc.org/standard/3dtiles/)
tilesets through dedicated scene endpoints so [CesiumJS](https://cesium.com/platform/cesiumjs/)
and other standards-based clients can load `tileset.json` and resolve all
relative tile, glTF, texture, and binary asset URIs without any client-side URL
rewriting.

This is the foundation slice of Honua's 3D support. It covers serving;
the [3D Tiles generation pipeline](scene-generation.md) (#842) covers
producing tilesets from PostGIS feature layers, while exporting hosted scenes
to OpenUSD/Omniverse is documented in the roadmap spike at
[OpenUSD and Omniverse Export Path](openusd-omniverse-export-path.md)
(`honua-server-901`; first-artifact implementation tracked in
`honua-server-904`).

## Public routes

| Route | Method | Purpose |
| --- | --- | --- |
| `/scenes/{sceneId}/tileset.json` | GET, HEAD | Root 3D Tiles document (`application/json`). |
| `/scenes/{sceneId}/{*assetPath}` | GET, HEAD | Tile, glTF, texture, JSON, or binary payload under the scene's asset prefix. |
| `/scenes/{sceneId}/access-envelope` | POST | Mint a short-lived signed access envelope for browser-safe rendering of a protected scene. Authorized callers only; public scenes return `400`. See [Browser-safe access via signed envelope](#browser-safe-access-via-signed-envelope). |

`sceneId` is operator-defined and stable; clients hard-code it the same way
they do for layer ids on other Honua endpoints. CesiumJS resolves nested
`uri` references inside `tileset.json` relative to the root URL, so
`./tiles/0/0/0.b3dm` inside the document maps directly to
`/scenes/{sceneId}/tiles/0/0/0.b3dm`.

## Registration

Scenes are registered through the [scene dataset registry admin API](../admin-api/scene-dataset-registry.md)
introduced in #844. The hosted serving routes resolve a dataset by its
URL slug via `ISceneDatasetRegistry.FindAsync`; the Postgres-backed
implementation behind that interface projects each record to the lean
`SceneDataset` shape served below.

The original `Scenes` configuration section remains in the codebase for
local-dev/test scenarios where Postgres is not available. In production it
is replaced at runtime by the registry; mixing both should be avoided. The
[NVIDIA construction demo fixture](../demo/nvidia-construction.md) is a
worked example of this fallback path — two scene datasets registered through
`Scenes:Datasets` against a committed fixture under
`tests/fixtures/scenes/nvidia-construction/`, served end-to-end without any
cloud dependency.

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
- **Protected scenes** (any `AccessPolicy`): `Cache-Control: private, max-age=...`.
  Credential-authorized responses include `Vary: Authorization, X-API-Key` so a
  private cache cannot reuse a response across requests where a different
  header carried the credential (Bearer/Basic-compat ride on `Authorization`;
  the canonical API key rides on `X-API-Key`). `?token=` responses vary by
  tokenized URL; `X-Honua-Token` responses include `Vary: X-Honua-Token`.
  Shared caches must not store the body — every request needs to re-run the
  dataset access policy — but a user agent's private cache may still
  revalidate within `max-age`.

The default TTLs are:

| Policy | Default | Override |
| --- | --- | --- |
| `tileset.json` metadata | 10 minutes | `OutputCache:SceneTilesetMetadata` |
| Tile / binary / texture asset | 1 hour | `OutputCache:SceneTileAsset` |

Datasets registered through the [scene dataset registry](../admin-api/scene-dataset-registry.md)
can override these defaults per-scene via `cachePolicy`. `maxAgeSeconds`
replaces the configured default for the matching response, and
`noStore = true` emits `Cache-Control: no-store` regardless of the global TTL —
useful for rotated debug datasets or short-lived previews. Protected no-store
responses still vary by the credential transport when a request header
authorized the body (`Authorization, X-API-Key` for credential-authorized
requests, or `X-Honua-Token` for native-header token transport). When the
response carries
`Cache-Control: no-store` the scene output-cache policies also suppress
server-side cache storage so a no-store body cannot be replayed on
subsequent requests until the configured TTL expires. The
configuration-driven `Scenes` fallback does not carry a per-dataset cache
policy, so those datasets always serve at the configured `OutputCache`
defaults.

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

### Browser-safe access via signed envelope

Protected scenes accept a short-lived **scene access envelope** in addition
to bearer/API-key headers. CesiumJS' resource loader cannot attach custom
`Authorization` headers or session cookies to nested tile fetches; the
envelope provides a render-safe alternative without weakening server-side
authorization.

#### Issue endpoint

```
POST /scenes/{sceneId}/access-envelope
Authorization: Bearer {credential}    # any standard Honua auth
```

Returns:

```json
{
  "sceneId": "downtown-protected",
  "token": "eyJzIjoiZG93bnRvd24tcHJvdGVjdGVkIiwiZSI6MTcxNDc1ODQwMH0.7c4f...",
  "expiresAt": "2026-05-03T14:00:00Z",
  "refreshAfter": "2026-05-03T13:52:30Z",
  "allowedMethods": ["GET", "HEAD"]
}
```

Issuance is itself gated by the dataset access policy: the caller must
already be authorized for the scene before a token is minted. Public scenes
return `400 Bad Request` from this endpoint (envelopes are unnecessary).
Issuance responses always carry `Cache-Control: no-store` — the token is a
short-lived credential and must not be persisted.

| Field | Description |
| --- | --- |
| `sceneId` | Echoes the requested scene id; the token is bound to this id and rejected on any other scene. |
| `token` | Opaque HMAC-signed wire string. Treat as a credential. |
| `expiresAt` | Absolute expiry (RFC 3339 / ISO 8601 UTC). After this instant the token is rejected with `401`. |
| `refreshAfter` | Recommended refresh instant, halfway through the TTL by default. Re-call the issue endpoint when reached so the active session never relies on a near-expiry token. |
| `allowedMethods` | HTTP methods the envelope is valid for. The current implementation issues `GET` and `HEAD`. |

#### Token transport on nested asset requests

Two transports are supported on protected scene asset endpoints:

- **`?token=` query parameter** — primary, browser-safe transport. CesiumJS
  propagates the parameter from the root `Cesium.Resource` to every nested
  tile/glTF/texture URL automatically.
- **`X-Honua-Token` header** — native-client transport for callers that
  prefer to keep tokens out of URLs and can attach headers to nested asset
  fetches.

CesiumJS integration:

```js
const tilesetResource = new Cesium.Resource({
  url: "https://honua.example.com/scenes/downtown-protected/tileset.json",
  queryParameters: { token: envelope.token }
});
const tileset = await Cesium.Cesium3DTileset.fromUrl(tilesetResource);
viewer.scene.primitives.add(tileset);
```

#### Validation outcomes

| Failure | HTTP status | Cause |
| --- | --- | --- |
| Missing token (no bearer) | `401` | Protected scene with no `Authorization` header and no token |
| Tampered or undecodable | `401` | Signature mismatch, bad encoding, or malformed payload |
| Expired | `401` | Token past `expiresAt` |
| Wrong scene | `403` | Token bound to a different scene id |
| Path traversal under valid token | `400` | Asset path resolver rejects the request before file I/O |

All failures return a shared problem-details body and never leak the token,
HMAC value, signing key, storage credentials, or absolute filesystem paths.

#### Response cache headers

Token-authorized asset responses use `Cache-Control: private, max-age=...`
so user-agent caches may revalidate within `max-age` but shared caches must
not store the body. Output caching is disabled at the server when a token
is present so cached anonymous bodies cannot be replayed across distinct
tokens. `?token=` responses vary by tokenized URL, while `X-Honua-Token`
responses include `Vary: X-Honua-Token`. `Vary: Authorization` is **not**
emitted for token-authorized responses (no `Authorization` header is present).

### Configuration and operational limits

```json
{
  "Honua": {
    "SceneAccessSigning": {
      "SigningKey": "set-from-secret-store-or-env",
      "TokenTtlMinutes": 15,
      "RefreshAfterFractionOfTtl": 0.5
    }
  }
}
```

| Property | Default | Description |
| --- | --- | --- |
| `SigningKey` | — (required for protected scenes) | HMAC-SHA256 secret. Resolve from environment variables or a secret store; never check the value into source. |
| `TokenTtlMinutes` | `15` | Envelope lifetime in minutes (1–1440). Shorter TTLs reduce credential exposure but force more refreshes. |
| `RefreshAfterFractionOfTtl` | `0.5` | Fraction of the TTL after which clients should refresh. |

Operational limits and security caveats:

- **Granularity.** Tokens bind to `(sceneId, expiresAt)`; one token grants
  access to all assets under the scene's prefix for the TTL window. This
  matches how Cesium loads thousands of nested assets per session and
  avoids per-tile signing round-trips.
- **No per-token revocation within TTL.** Within the TTL window a token
  cannot be individually revoked. Use a short TTL to bound the exposure
  window. Rotating `SigningKey` invalidates **all** outstanding tokens
  immediately on the next request.
- **Query-parameter logging.** Tokens transported via `?token=` may appear
  in server access logs; deployments that route through proxies that log
  full URLs should prefer the `X-Honua-Token` header on native clients and
  keep the TTL short.
- **No subject binding.** The current envelope does not embed the issuing
  user's identity. Per-user revocation is not in scope; rely on TTL and
  signing-key rotation.
- **No cross-scene reuse.** A token issued for scene `A` is rejected with
  `403` when used on scene `B`.

> Browser smoke coverage for end-to-end Cesium rendering of protected
> scenes lives in [honua-server-838](https://github.com/honua-io/honua-server/issues/838).

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
