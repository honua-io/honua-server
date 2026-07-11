# Portal Facade Seed & Bootstrap Contract

_Epic #1240 (ArcGIS Portal/Sharing facade) · child #1372 (conformance) · child #1373 (client repoint)._

This document is the **server-owned contract** that downstream consumers — the
`honua-sdk-js` `@honua/sdk-esri-compat` repoint path, the `honua-sdk-dotnet` /
`honua-sdk-python` clients, the `arcgis-stub` `portal` conformance lane, and the
licensed ArcGIS Pro / Field Maps evidence runs — target when validating that a
packaged Esri client can discover and open Honua content as Portal items.

Per the repo ownership split (`CLAUDE.md`): **honua-server owns the seed +
bootstrap contract and the release-proof interop; the SDK repos own the
client-side implementation.** This is that contract.

## What "Portal-enabled" means

The facade is a thin, opt-in projection over the existing server:

- **Off by default.** The read surface is gated by the `identity.portal-sharing`
  entitlement (Community edition) and the `Sharing:ReadSurface:Enabled` flag
  (default `true`); token issuance by `identity.portal-token` +
  `Authentication:PortalToken:Enabled`. All endpoints return **404** when
  disabled, so no discovery surface is exposed unintentionally.
- **HTTPS-only token issuance.** `generateToken` and the OAuth2 bridge require
  HTTPS unless `Authentication:PortalToken:RequireHttps=false` (dev/test only —
  set in `docker/client-compat/compose.yml` for the HTTP docker network).
- **RBAC-projected access.** A Portal item's `access` string
  (`public`/`org`/`private`) is a projection of the shared RBAC decision
  (`IAccessPolicyEvaluator`), never a second ACL. See `PortalItemProjector`.

## Seed fixture

Canonical fixture: [`tests/seed/portal-compat.yaml`](../../tests/seed/portal-compat.yaml),
applied by `docker/client-compat/seed/run.sh` after the base SQL seed (which owns
`honua.seed_metadata_v2_compat_snapshot()`). It seeds three FeatureServer services
spanning the access ladder:

| Service (`service_name`) | `accessPolicy` | Projected `access` | Anonymous | Named user (no role) | Role `portal-admin` |
|---|---|---|---|---|---|
| `portal_public`  | `allowAnonymous: true` | `public`  | ✅ visible | ✅ visible | ✅ visible |
| `portal_org`     | `allowAnonymous: false` | `org`     | ❌ hidden | ✅ visible | ✅ visible |
| `portal_private` | `allowAnonymous: false`, `allowedRoles: [portal-admin]` | `private` | ❌ hidden | ❌ hidden | ✅ visible |

> **Note on Metadata v2 fan-out.** `seed_metadata_v2_compat_snapshot()` projects
> each seeded service into `feature`/`map`/`image` Esri service nodes (item ids
> `svc-<name>-feature`, `-map`, `-image`). Consumers should therefore be
> **data-driven**: discover item ids via `search`, do not hard-code them.

## Bootstrap / discovery sequence (the contract SDK CI targets)

A client repoints by pointing at `<base>/sharing/rest` and following the same
sequence ArcGIS Pro "Add Portal" and Field Maps issue:

1. `GET /sharing/rest/info?f=json`
   → `{ "authInfo": { "isTokenBasedSecurity": true, "tokenServicesUrl": "<base>/sharing/rest/generateToken" } }`
   (no `currentVersion`/`fullVersion` — Honua does not impersonate an ArcGIS release).
2. `GET /sharing/rest/portals/self?f=json`
   → `{ "id", "isPortal": true, "name", "portalName", "user": <block|null> }`.
   The `user` block is populated only when a token authenticates the request.
3. `POST /sharing/rest/generateToken` (form: `username`, `password`, `client=requestip`, `f=json`)
   → `{ "token": string, "expires": <Unix MILLISECONDS>, "ssl": true }`.
   **`expires` is milliseconds, not seconds** — a client reading it as seconds
   breaks. (OAuth2 named-user login is the alternate path:
   `GET /sharing/rest/oauth2/authorize` → `POST /sharing/rest/oauth2/token` →
   `{ "access_token", "expires_in": <SECONDS>, "refresh_token"?, "token_type": "Bearer" }`.)
4. `GET /sharing/rest/search?f=json&num=25[&token=<token>]`
   → `{ "total", "start"(1-based), "num", "nextStart"(-1 on last page), "results": PortalItem[] }`.
   Anonymous callers see only `public` items; a named-user token additionally
   surfaces `org` items; `portal-admin` additionally surfaces `private`.
5. `GET /sharing/rest/content/items/{id}?f=json[&token=<token>]`
   → a `PortalItem` whose `url` points at the existing
   `<base>/rest/services/{name}/FeatureServer`. Open that URL with the same token
   (`token=` query param or `Authorization: Bearer`) to read features end-to-end.

`PortalItem` shape: `id, owner, created(ms), modified(ms), type("Feature Service"|"Map Service"|"Image Service"), typeKeywords[], title, snippet, description, tags[], url, access("public"|"org"|"private"), extent([[xmin,ymin],[xmax,ymax]]), spatialReference, culture, numComments, numViews`.

Errors follow the Esri envelope `{ "error": { "code", "message", "details" } }`.

## Conformance & evidence

- **Automated (unlicensed):** the `arcgis-stub` `portal` protocol lane
  (`docker/client-compat/arcgis-stub/stub_runner.py`, envelope
  `tests/baselines/client-compat/arcgis-stub/arcgis-stub-portal.cert.json`)
  exercises the sequence above as CERT-PRTL-\* — see the
  [certification matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md#arcgis-portal-facade-lane-arcgis-stub-portal-protocol).
- **Contract/integration (server):**
  `tests/dotnet/Honua.Server.Tests/Features/Sharing/PortalFacadeDiscoveryContractTests.cs`
  asserts the tiered projection and Esri wire shapes in-process.
- **Licensed (real client):** the ArcGIS Pro / Field Maps evidence runs point at a
  Portal-enabled deployment seeded with this fixture and emit a
  `client_lane: "desktop-arcgis"` envelope — see
  [`ARCGIS_PRO_LICENSED_EVIDENCE.md`](../internal/evidence/ARCGIS_PRO_LICENSED_EVIDENCE.md).
  This is an operator-provisioned run (self-hosted Windows + licensed ArcGIS Pro),
  tracked in #1372/#1096.

## Consuming repos

- `honua-sdk-js` (`@honua/sdk-esri-compat`) — Portal repoint client
  ([honua-sdk-js#383](https://github.com/honua-io/honua-sdk-js/pull/383)) and
  current×current `sdk-server-compatibility` coverage (#2614).
- `honua-sdk-dotnet` — typed Portal repoint path tracked in
  [honua-sdk-dotnet#257](https://github.com/honua-io/honua-sdk-dotnet/issues/257).
- `honua-sdk-python` — async/generated-sync Portal repoint path tracked in
  [honua-sdk-python#168](https://github.com/honua-io/honua-sdk-python/issues/168).

When changing this contract (service names, tiers, or the `portals/self` /
`PortalItem` shape), bump the fixture and this doc together and notify the SDK
repos via the cross-links on #1373.
