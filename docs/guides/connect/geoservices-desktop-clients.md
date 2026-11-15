---
type: guide
title: "Connect a GeoServices REST desktop client to Honua"
description: "Add a Honua-hosted feature layer or map service to a desktop GIS project through the GeoServices REST /rest/services endpoints, with optional portal-style token auth."
resource: "honua://capability/identity.portal-token"
---
# Connect a GeoServices REST desktop client to Honua

Add a Honua-hosted feature layer or map service to a desktop GIS project through the GeoServices REST `/rest/services` endpoints, with optional portal-style token auth. Honua works with ArcGIS Pro and other desktop clients that consume GeoServices REST services; the steps below use ArcGIS Pro's menu names.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). Desktop clients and their SDKs keep their own vendor licensing; Honua replaces the server, not the client licence.

Honua exposes each published service as:

- Feature data: `http://localhost:8080/rest/services/{serviceId}/FeatureServer`
- Rendered maps: `http://localhost:8080/rest/services/{serviceId}/MapServer`
- Raster coverages (WCS 2.0.1): `http://localhost:8080/ogc/wcs/{serviceId}`

## Steps

1. Open a map in the desktop client.
2. Add data from a path (for example **Map** ribbon, **Add Data → Data From Path**).
3. Paste the FeatureServer layer URL, `http://localhost:8080/rest/services/{serviceId}/FeatureServer/{layerId}`, and add it. Layer ids are the ids in the service's `layers` list (`FeatureServer?f=json`), not zero-based positions. Honua does not renumber layers per service, so read the id from that list rather than assuming `0` or `1`. The layer draws and appears in the table of contents.
4. Repeat with the `MapServer` URL to add the server-rendered map service instead.
5. To filter, set a definition query on the layer, such as `OBJECTID > 0`; Honua's `query` endpoint evaluates the expression server-side.

### Raster coverages over WCS

Add a WCS server connection with `http://localhost:8080/ogc/wcs/{serviceId}` and version 2.0.1.

Use the `/ogc/wcs/{serviceId}` form, not `/ogc/services/{serviceId}/wcs`. Over HTTPS, some desktop clients treat a URL containing `/services/{name}/` as a GeoServices REST server site, and the connection fails. See [connecting desktop clients](../../reference/protocols/wms-wfs-wcs-wmts.md#connecting-desktop-gis-clients).

### Token auth (if authentication is enabled)

Honua implements the portal token endpoint at `/sharing/rest/generateToken` (GET or POST form-encoded). Desktop clients that prompt for credentials when adding a secured service use it automatically. For an application-managed credential, use `PortalCompat.generateToken` from `@honua/sdk-js/client-compat`:

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

### Native SDKs

The same URLs work in native GeoServices REST client SDKs, for example a JavaScript feature layer:

```js
const layer = new FeatureLayer({
  url: "http://localhost:8080/rest/services/my-service/FeatureServer/1" // a layer id from the service's layers list
});
```

## Verify

Confirm the service metadata and a query respond before blaming the client:

Open `http://localhost:8080/rest/services/{service}/FeatureServer?f=json` and `http://localhost:8080/rest/services/{service}/FeatureServer/{layerId}/query?where=1%3D1&resultRecordCount=1&f=json` in a browser, substituting the service id and a layer id from the first response's `layers` list. The first response describes the service and the second returns one feature.

In the desktop client, the layer should draw, the attribute table should open, and a definition query should change the feature count.

## Troubleshoot

- **"Cannot add data" / 404 on the URL**: check the service id with `honua services`, and use a layer id from the service's `layers` list rather than `0` (an unknown layer returns error code 404, "Service layer was not found"). See [troubleshooting](../deploy/troubleshooting.md).
- **Credential prompt loops, 401, or error code 499**: an anonymous request to a secured service returns a JSON error with code `499` ("Unauthorized") in an HTTP 200 response, and the service list at `/rest/services` omits services the caller cannot read. Run the `PortalCompat.generateToken` example in **Token auth** above with the same account. Token issuance returns 403 over plain HTTP unless `RequireHttps` is disabled.
- **`generateToken` returns 402 Payment Required**: the `identity.portal-token` entitlement is not active in your edition configuration.
- **WCS connection fails**: the URL contains `/services/`. Use `/ogc/wcs/{serviceId}` (see **Raster coverages over WCS** above).
- **Layer draws but some operations fail**: Honua implements broad but not total GeoServices REST parity; check the operation in the [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md) before debugging further.
- **Scene layers (I3S/SceneServer) return 404 or 402**: the default is 404 until experimental capability `serve.i3s-scene` is enabled; an enabled route without the Enterprise entitlement returns 402.

## Next steps

- [GeoServices REST parity detail](../../reference/compatibility/geoservices-parity.md)
- [Migrate GeoServices REST services](../migrate/from-geoservices-rest.md)
- [Query features](../query-analyze/query-features.md)

Esri, ArcGIS, and the Esri product names used here are trademarks, registered trademarks, or service marks of Esri in the United States and other countries. They identify the products Honua is compatible with; Honua is not affiliated with, sponsored by, or endorsed by Esri.
