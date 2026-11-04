---
type: guide
title: "Repoint GeoServices REST apps and SDKs at Honua"
description: "You'll move existing GeoServices REST client applications (web apps, desktop GIS clients and native SDKs) onto Honua-served endpoints without rewriting them."
---
# Repoint GeoServices REST apps and SDKs at Honua

You'll move existing GeoServices REST client applications (web apps, desktop GIS clients and native SDKs) onto Honua-served endpoints without rewriting them. Honua works with ArcGIS Pro, the ArcGIS Maps SDKs and other clients of the GeoServices REST API.

**Prerequisites:** a published Honua service (for example from [migrate GeoServices REST services](from-geoservices-rest.md)) and, for web apps, Node.js 22+ in the app's build environment.

Honua serves the GeoServices REST API at `/rest/services/{serviceId}/FeatureServer` and `/MapServer` plus a portal-style token endpoint at `/sharing/rest/generateToken`, so most clients only need a new URL ([parity reference](../../reference/compatibility/geoservices-parity.md)).

## Steps

### 1. Map the service URLs

List the published services and layers with the JavaScript SDK's `honua` CLI:

> `honua` here is the JavaScript SDK's CLI, which is the one with `query`. Install it with
> `npm i -g @honua/sdk-js`, or run it ad hoc as `npx -p @honua/sdk-js honua …`. The `-p`
> matters, because npx otherwise looks for a binary named `sdk-js`. The PyPI `honua-sdk`
> distribution installs an executable with the same name and no `query` subcommand, so on a
> machine with both, prefer the `npx -p` form.

```bash
export HONUA_BASE_URL="$HONUA_URL"
honua services
honua layers "$SERVICE_ID"
```

The URL shape changes: a source URL like `https://gis.example.com/arcgis/rest/services/Public/Parcels/FeatureServer/0` becomes `$HONUA_URL/rest/services/parcels/FeatureServer/0`, with one service id per published Honua service and no folder segment. Honua also answers under the `/arcgis` site-root alias ([service path prefix](../../gis/service-path-prefix.md)), but new configuration should use the root form. Build the old-URL → new-URL table for every service before touching client code.

### 2. Point JavaScript apps at the compat layer

```bash
npm install @honua/sdk-js
```

The `@honua/sdk-js/client-compat` subpath ships compatibility wrappers (`FeatureLayerCompat`, `MapImageLayerCompat`, `MapViewCompat`, `WebMapCompat`, plus `MapCompat`, `SceneViewCompat`, `TileLayerCompat`, and widget/geometry/renderer compat classes) that keep existing JavaScript client code patterns working against Honua:

```js
import { FeatureLayerCompat } from "@honua/sdk-js/client-compat";

const layer = new FeatureLayerCompat({
  url: "http://localhost:8080/rest/services/parcels/FeatureServer/0",
  outFields: ["*"],
  definitionExpression: "STATUS = 'ACTIVE'",
});
const result = await layer.queryFeatures({ where: "1=1" });
```

`FeatureLayerCompat` supports the common constructor options (`id`, `title`, `outFields`, `definitionExpression`, `renderer`, `popupTemplate`, `labelingInfo`, `opacity`, `visible`, `minScale`, `maxScale`) plus paged helpers like `queryFeaturesAll()` and `queryFeaturesStream()`; `MapImageLayerCompat` covers `exportImage`, `identify`, `find`, `getLegend`, and sublayer queries. The full supported-option list per class is in the [SDK guide](https://github.com/honua-io/honua-sdk-js/blob/trunk/docs/guide.md). `labelingInfo` in that list is a client constructor option. The server does not generate label classes or paint them. FeatureServer metadata returns `labelingInfo` only when an author stored it on the layer's `drawingInfo`. See [Feature labeling is not served](../style/style-maps.md#feature-labeling-is-not-served).

### 3. Run the JavaScript migration engine

```bash
npm install --global @honua/honua-migrate
honua-js-migrate scan ./src --report scan-report.json
honua-js-migrate codemod ./src --write --annotate-todos --report migration-report.json
```

The codemod is intentionally conservative: it rewrites safe constructor sites (`new FeatureLayer(...)` → `new FeatureLayerCompat(...)` and the other classes above), skips complex sites and CommonJS `require(...)` usage, and records those as manual TODO entries in the report (`--annotate-todos` also injects inline `// TODO(honua-migrate)` comments). CI gating flags such as `--fail-on-manual` and `--max-manual-ratio 0.2` let you block merges until the manual backlog is worked off.

### 4. Repoint desktop clients and native SDKs

Use `honua query "$SERVICE_ID/0" --limit 1` to confirm the repointed service before changing desktop or native-client configuration.

Desktop and native SDK clients need no compat layer: paste the Honua `FeatureServer`/`MapServer` URL wherever the source URL was configured. Step-by-step instructions, including portal-style sign-in, are in [connect a GeoServices REST desktop client](../connect/geoservices-desktop-clients.md). Desktop clients and native SDKs keep their own vendor licensing; Honua replaces the server, not the client licence.

### 5. Update token authentication

Use `PortalCompat.generateToken` from `@honua/sdk-js/client-compat`, or let the desktop client prompt for credentials and discover `/sharing/rest/generateToken` automatically. Supply the username, password, client binding, referer, and expiration through the client API.

Clients that fetched tokens from a source server's `/arcgis/tokens/` or portal `generateToken` URL must point at Honua's `/sharing/rest/generateToken` instead; clients that prompt for credentials discover it automatically. The opaque token is accepted on `/rest/services/*` requests as `?token=`, `Authorization: Bearer`, `X-Esri-Authorization: Bearer`, or a form-encoded POST `token` field. Token issuance is HTTPS-only by default.

## What works unchanged vs what needs attention

| Area | Status |
|---|---|
| FeatureServer query, edits, attachments, related records | Works unchanged at the Honua URL ([parity detail](../../reference/compatibility/geoservices-parity.md)). |
| MapServer export, identify, find, legend, tiles | Works unchanged; MapServer WMTS supports WebMercatorQuad, WorldCRS84Quad, and configured custom matrix sets. |
| Leaflet and REST-client libraries for GeoServices REST | Work by URL swap; no codemod needed. |
| Service URL shape | Needs attention: one service id per service, no folders; remap every URL (step 1). |
| Token endpoint URL | Needs attention: move to `/sharing/rest/generateToken` (step 5). |
| Change tracking, true curves, 3D queries (FeatureServer) | Incremental Postgres change tracking, true-curve input densification, and Z/M queries are implemented; other stores use snapshot sync and curves are not preserved losslessly on output. |
| Portal item/group browsing, scene layers (I3S) | Limited sharing surface; I3S is experimental, off by default, and Enterprise-gated. Verify against the parity reference. |

## Verify

```bash
export HONUA_BASE_URL="$HONUA_URL"
honua query "$SERVICE_ID/0" --count
```

The count should match the source service, the repointed app should draw the layer and answer queries, and `honua-js-migrate codemod ./src --report migration-report.json` (dry run) should report no remaining unhandled client sites.

## Troubleshoot

- **404 on the new URL**: run `honua services` and use the returned service id; remember there are no folder segments.
- **Credential prompt loops or 401**: test `/sharing/rest/generateToken` directly (step 5); issuance returns 403 over plain HTTP unless `RequireHttps` is disabled for local testing.
- **A specific operation fails after the URL swap**: look it up in the [parity reference](../../reference/compatibility/geoservices-parity.md); partial-parity operations need a workaround, not client debugging.
- **Codemod leaves many manual TODOs**: CommonJS modules and complex constructor sites are skipped by design; work the report entries by hand or gate them with `--max-manual-ratio`.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Connect a GeoServices REST desktop client](../connect/geoservices-desktop-clients.md): desktop walkthrough with token auth.
- [Migrate GeoServices REST services](from-geoservices-rest.md): move the data and services themselves.
- [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md): operation-level compatibility detail.

Esri, ArcGIS, and the Esri product names used here are trademarks, registered trademarks, or service marks of Esri in the United States and other countries. They identify the products Honua is compatible with; Honua is not affiliated with, sponsored by, or endorsed by Esri.
