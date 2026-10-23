---
type: guide
title: "Connect ArcGIS Pro to Honua"
description: "Add a Honua-hosted feature layer or map service to an ArcGIS Pro project using the ArcGIS-compatible /rest/services endpoints, with optional portal-style token auth."
resource: "honua://capability/identity.portal-token"
---
# Connect ArcGIS Pro to Honua

Add a Honua-hosted feature layer or map service to an ArcGIS Pro project using the ArcGIS-compatible `/rest/services` endpoints, with optional portal-style token auth.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). ArcGIS Pro itself (and the ArcGIS Maps SDKs) require a valid Esri license — Honua replaces the server, not the Esri client licensing.

Honua exposes each published service as:

- Feature data: `http://localhost:8080/rest/services/{serviceId}/FeatureServer`
- Rendered maps: `http://localhost:8080/rest/services/{serviceId}/MapServer`
- Raster coverages (WCS 2.0.1): `http://localhost:8080/ogc/wcs/{serviceId}`

## Steps

1. Open a map in ArcGIS Pro.
2. On the **Map** ribbon, click **Add Data → Data From Path**.
3. Paste the FeatureServer layer URL, for example `http://localhost:8080/rest/services/my-service/FeatureServer/0`, and click **Add**. The layer draws and appears in the Contents pane.
4. Repeat with the `MapServer` URL to add the server-rendered map service instead.
5. To filter, right-click the layer → **Properties → Definition Query** and add a query such as `OBJECTID > 0`; the expression is evaluated server-side by Honua's `query` endpoint.

### Raster coverages over WCS

On the **Insert** ribbon, click **Connections → Server → New WCS Server** and enter
`http://localhost:8080/ogc/wcs/{serviceId}` with version 2.0.1. In ArcPy, use the same URL
with **Make WCS Layer**, for example
`arcpy.management.MakeWCSLayer("http://localhost:8080/ogc/wcs/my-service?coverage=coverage_0&version=2.0.1", "coverage")`.

Use the `/ogc/wcs/{serviceId}` form, not `/ogc/services/{serviceId}/wcs`. Over HTTPS,
ArcGIS Pro treats a URL containing `/services/{name}/` as an ArcGIS Server site, and the
connection fails with `ERROR 999999`. See [Connecting ArcGIS Pro](../../reference/protocols/wms-wfs-wcs-wmts.md#connecting-arcgis-pro).

### Token auth (if authentication is enabled)

Honua implements the ArcGIS Portal token endpoint at `/sharing/rest/generateToken` (GET or POST form-encoded). Esri clients that prompt for credentials when adding a secured service use it automatically. For an application-managed credential, use `PortalCompat.generateToken` from `@honua/sdk-js/esri-compat`:

```js
const credential = await portal.generateToken({
  username,
  password,
  client: "referer",
  referer: "http://localhost:8080",
});
```

The credential contains `token`, `expires`, and `ssl`.

Reuse the token on any `/rest/services/*` request as `?token=<opaque>`, `Authorization: Bearer <opaque>`, `X-Esri-Authorization: Bearer <opaque>`, or a form-encoded POST `token` field. Token issuance is HTTPS-only by default; see your deployment's auth configuration if you need it on plain HTTP for local testing.

### Esri SDKs

The same URLs work in the ArcGIS Maps SDKs (subject to Esri SDK licensing), for example the JavaScript SDK:

```js
const layer = new FeatureLayer({
  url: "http://localhost:8080/rest/services/my-service/FeatureServer/0"
});
```

## Verify

Confirm the service metadata and a query respond before blaming the client:

Open `http://localhost:8080/rest/services/{service}/FeatureServer?f=json` and `http://localhost:8080/rest/services/{service}/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&f=json` in a browser, substituting the service id. The first response describes the service and the second returns one feature.

In ArcGIS Pro, the layer should draw, the attribute table should open, and a definition query should change the feature count.

## Troubleshoot

- **"Cannot add data" / 404 on the URL** — check the service id with `honua services`. See [troubleshooting](../deploy/troubleshooting.md).
- **Credential prompt loops or 401** — run the `PortalCompat.generateToken` example in **Token auth** above with the same account. Token issuance returns 403 over plain HTTP unless `RequireHttps` is disabled.
- **`generateToken` returns 402 Payment Required** — the `identity.portal-token` entitlement is not active in your edition configuration.
- **WCS connection fails with `ERROR 999999`** — the URL contains `/services/`. Use `/ogc/wcs/{serviceId}` (see **Raster coverages over WCS** above).
- **Layer draws but some operations fail** — Honua implements broad but not total GeoServices parity; check the operation in the [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md) before debugging further.
- **Scene layers (I3S/SceneServer) return 404 or 402** — the default is 404 until experimental capability `serve.i3s-scene` is enabled; an enabled route without the Enterprise entitlement returns 402.

## Next steps

- [GeoServices REST parity detail](../../reference/compatibility/geoservices-parity.md)
- [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md)
- [Query features](../query-analyze/query-features.md)
