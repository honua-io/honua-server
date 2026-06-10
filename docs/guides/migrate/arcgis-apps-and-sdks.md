# Repoint ArcGIS apps and SDKs at Honua

You'll move existing ArcGIS client applications — web apps, ArcGIS Pro, and the ArcGIS Maps SDKs — onto Honua-served endpoints without rewriting them.

**Prerequisites:** a published Honua service (for example from [migrate from ArcGIS Server](from-arcgis-server.md)) and, for web apps, Node.js 18+ in the app's build environment.

Honua serves the GeoServices REST API at `/rest/services/{serviceId}/FeatureServer` and `/MapServer` plus a portal-style token endpoint at `/sharing/rest/generateToken`, so most Esri clients only need a new URL ([parity reference](../../reference/compatibility/geoservices-parity.md)).

## Steps

### 1. Map the service URLs

```bash
HONUA_URL=http://localhost:8080
SERVICE_ID=parcels
curl -s "$HONUA_URL/rest/services/$SERVICE_ID/FeatureServer?f=json" | jq .layers
```

The URL shape changes: ArcGIS Server URLs like `https://gis.example.com/arcgis/rest/services/Public/Parcels/FeatureServer/0` become `$HONUA_URL/rest/services/parcels/FeatureServer/0` — there is no `/arcgis` prefix and no folder segment, just one service id per published Honua service. Build the old-URL → new-URL table for every service before touching client code.

### 2. Point ArcGIS JS apps at the compat layer

```bash
npm install @honua/sdk-js
```

The `@honua/sdk-js/esri-compat` subpath ships Esri-style wrappers — `FeatureLayerCompat`, `MapImageLayerCompat`, `MapViewCompat`, `WebMapCompat`, plus `MapCompat`, `SceneViewCompat`, `TileLayerCompat`, and widget/geometry/renderer compat classes — that keep ArcGIS JS (`@arcgis/core`) code patterns working against Honua:

```js
import { FeatureLayerCompat } from "@honua/sdk-js/esri-compat";

const layer = new FeatureLayerCompat({
  url: "http://localhost:8080/rest/services/parcels/FeatureServer/0",
  outFields: ["*"],
  definitionExpression: "STATUS = 'ACTIVE'",
});
const result = await layer.queryFeatures({ where: "1=1" });
```

`FeatureLayerCompat` supports the common constructor options (`id`, `title`, `outFields`, `definitionExpression`, `renderer`, `popupTemplate`, `labelingInfo`, `opacity`, `visible`, `minScale`, `maxScale`) plus paged helpers like `queryFeaturesAll()` and `queryFeaturesStream()`; `MapImageLayerCompat` covers `exportImage`, `identify`, `find`, `getLegend`, and sublayer queries. The full supported-option list per class is in the [SDK guide](https://github.com/honua-io/honua-sdk-js/blob/main/docs/guide.md).

### 3. Run the honua-migrate codemod

```bash
npx @honua/honua-migrate scan ./src --report scan-report.json
npx @honua/honua-migrate codemod ./src --write --annotate-todos --report migration-report.json
```

The codemod is intentionally conservative: it rewrites safe constructor sites (`new FeatureLayer(...)` → `new FeatureLayerCompat(...)` and the other classes above), skips complex sites and CommonJS `require(...)` usage, and records those as manual TODO entries in the report (`--annotate-todos` also injects inline `// TODO(honua-migrate)` comments). CI gating flags such as `--fail-on-manual` and `--max-manual-ratio 0.2` let you block merges until the manual backlog is worked off.

### 4. Repoint ArcGIS Pro and the ArcGIS Maps SDKs

```bash
curl -s "$HONUA_URL/rest/services/$SERVICE_ID/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&f=json" | jq .features
```

Desktop and native SDK clients need no compat layer — paste the Honua `FeatureServer`/`MapServer` URL wherever the ArcGIS URL was configured; step-by-step instructions including portal-style sign-in are in [connect ArcGIS Pro](../connect/arcgis-pro.md). ArcGIS Pro and the ArcGIS Maps SDKs still require a valid Esri license — Honua replaces the server, not the Esri client licensing.

### 5. Update token authentication

```bash
curl -s -X POST "$HONUA_URL/sharing/rest/generateToken" \
  -d "username=$HONUA_USER" -d "password=$HONUA_PASSWORD" \
  -d "client=referer" -d "referer=$HONUA_URL" -d "f=json"
```

Clients that fetched tokens from an ArcGIS Server `/arcgis/tokens/` or Portal `generateToken` URL must point at Honua's `/sharing/rest/generateToken` instead; Esri clients that prompt for credentials discover it automatically. The opaque token is accepted on `/rest/services/*` requests as `?token=`, `Authorization: Bearer`, or `X-Esri-Authorization: Bearer`. Token issuance is HTTPS-only by default.

## What works unchanged vs what needs attention

| Area | Status |
|---|---|
| FeatureServer query, edits, attachments, related records | Works unchanged at the Honua URL ([parity detail](../../reference/compatibility/geoservices-parity.md)). |
| MapServer export, identify, find, legend, tiles | Works unchanged; WMTS is WebMercatorQuad-only. |
| Esri Leaflet, `arcgis-rest-js` | Work by URL swap — no codemod needed. |
| Service URL shape | Needs attention: no `/arcgis` prefix, no folders; remap every URL (step 1). |
| Token endpoint URL | Needs attention: move to `/sharing/rest/generateToken` (step 5). |
| Change tracking, true curves, 3D queries (FeatureServer) | Not implemented — check the parity reference before relying on them. |
| Portal item/group browsing, scene layers (I3S) | Limited sharing surface; I3S is Enterprise-gated — verify against the parity reference. |

## Verify

```bash
curl -s "$HONUA_URL/rest/services/$SERVICE_ID/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"
```

The count should match the source service, the repointed app should draw the layer and answer queries, and `npx @honua/honua-migrate codemod ./src --report migration-report.json` (dry run) should report no remaining unhandled ArcGIS sites.

## Troubleshoot

- **404 on the new URL** — the service id is wrong: `curl $HONUA_URL/rest/services?f=json` lists published services; remember there are no folder segments.
- **Credential prompt loops or 401** — test `/sharing/rest/generateToken` directly (step 5); issuance returns 403 over plain HTTP unless `RequireHttps` is disabled for local testing.
- **A specific operation fails after the URL swap** — look it up in the [parity reference](../../reference/compatibility/geoservices-parity.md); partial-parity operations need a workaround, not client debugging.
- **Codemod leaves many manual TODOs** — CommonJS modules and complex constructor sites are skipped by design; work the report entries by hand or gate them with `--max-manual-ratio`.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Connect ArcGIS Pro](../connect/arcgis-pro.md) — desktop walkthrough with token auth.
- [Migrate from ArcGIS Server](from-arcgis-server.md) — move the data and services themselves.
- [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md) — operation-level compatibility detail.
