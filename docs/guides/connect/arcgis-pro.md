# Connect ArcGIS Pro to Honua

Add a Honua-hosted feature layer or map service to an ArcGIS Pro project using the ArcGIS-compatible `/rest/services` endpoints, with optional portal-style token auth.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). ArcGIS Pro itself (and the ArcGIS Maps SDKs) require a valid Esri license — Honua replaces the server, not the Esri client licensing.

Honua exposes each published service as:

- Feature data: `http://localhost:8080/rest/services/{serviceId}/FeatureServer`
- Rendered maps: `http://localhost:8080/rest/services/{serviceId}/MapServer`

## Steps

1. Open a map in ArcGIS Pro.
2. On the **Map** ribbon, click **Add Data → Data From Path**.
3. Paste the FeatureServer layer URL, for example `http://localhost:8080/rest/services/my-service/FeatureServer/0`, and click **Add**. The layer draws and appears in the Contents pane.
4. Repeat with the `MapServer` URL to add the server-rendered map service instead.
5. To filter, right-click the layer → **Properties → Definition Query** and add a query such as `OBJECTID > 0`; the expression is evaluated server-side by Honua's `query` endpoint.

### Token auth (if authentication is enabled)

Honua implements the ArcGIS Portal token endpoint at `/sharing/rest/generateToken` (GET or POST form-encoded). Esri clients that prompt for credentials when adding a secured service use it automatically. To request a token manually:

```bash
BASE=http://localhost:8080
curl -s -X POST "$BASE/sharing/rest/generateToken" \
  -d "username=$HONUA_USER" -d "password=$HONUA_PASSWORD" \
  -d "client=referer" -d "referer=$BASE" -d "f=json"
# => { "token": "<opaque>", "expires": 1735689600000, "ssl": true }
```

Reuse the token on any `/rest/services/*` request as `?token=<opaque>`, `Authorization: Bearer <opaque>`, or `X-Esri-Authorization: Bearer <opaque>`. Token issuance is HTTPS-only by default; see your deployment's auth configuration if you need it on plain HTTP for local testing.

### Esri SDKs

The same URLs work in the ArcGIS Maps SDKs (subject to Esri SDK licensing), for example the JavaScript SDK:

```js
const layer = new FeatureLayer({
  url: "http://localhost:8080/rest/services/my-service/FeatureServer/0"
});
```

## Verify

Confirm the service metadata and a query respond before blaming the client:

```bash
BASE=http://localhost:8080
SERVICE=my-service
curl -s "$BASE/rest/services/$SERVICE/FeatureServer?f=json" | head
curl -s "$BASE/rest/services/$SERVICE/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&f=json" | head
```

In ArcGIS Pro, the layer should draw, the attribute table should open, and a definition query should change the feature count.

## Troubleshoot

- **"Cannot add data" / 404 on the URL** — check the service id: `curl http://localhost:8080/rest/services?f=json` lists published services. See [troubleshooting](../deploy/troubleshooting.md).
- **Credential prompt loops or 401** — verify the account works against `/sharing/rest/generateToken` directly (command above). Token issuance returns 403 over plain HTTP unless `RequireHttps` is disabled.
- **`generateToken` returns 402 Payment Required** — the `identity.portal-token` entitlement is not active in your edition configuration.
- **Layer draws but some operations fail** — Honua implements broad but not total GeoServices parity; check the operation in the [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md) before debugging further.
- **Scene layers (I3S/SceneServer) return 402** — I3S serving is Enterprise-gated and not part of open-core.

## Next steps

- [GeoServices REST parity detail](../../reference/compatibility/geoservices-parity.md)
- [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md)
- [Query features](../query-analyze/query-features.md)
