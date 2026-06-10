# Import from ArcGIS services

You'll have a layer from a public ArcGIS REST service copied into PostGIS and published in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)). Queued imports require the distributed job queue (Redis) to be healthy.

The GeoServices import API discovers layers on a live ArcGIS Server / ArcGIS Online service, pages features into a PostGIS table, and auto-publishes the result as a Honua layer. This guide covers a simple one-service import; for full migrations (inventory scans, styles, batches) see [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md).

> Prefer an SDK? The same endpoints are wrapped by `honua-sdk-js` and `honua-sdk-dotnet`. Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Discover the service

```bash
HONUA_URL=http://localhost:8080
HONUA_API_KEY=your-admin-api-key
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{"serviceUrl":"https://services.arcgis.com/example/arcgis/rest/services/Parcels/FeatureServer","timeoutSeconds":30}' \
  "$HONUA_URL/api/v1/admin/import/geoservices/discover"
```

Use an HTTPS service root URL ending in `FeatureServer` or `MapServer` (layer URLs are rejected). The response lists each layer's `id`, `name`, `geometryType`, and `featureCount`.

### 2. Start the import job

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "serviceUrl": "https://services.arcgis.com/example/arcgis/rest/services/Parcels/FeatureServer",
    "layerId": 0,
    "tableName": "parcels",
    "targetSrid": 4326,
    "overwriteExisting": true,
    "autoPublish": true
  }' \
  "$HONUA_URL/api/v1/admin/import/geoservices/start"
```

Returns `202 Accepted` with a `jobId`. Optional fields: `whereClause` and `outputFields` to filter what is copied, `targetSchema`, `batchSize`, `serviceName` (target Honua service for auto-publishing; `autoPublish` defaults to `true`).

### 3. Poll the job

```bash
JOB_ID=paste-jobid-from-step-2
curl -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/import/geoservices/jobs/$JOB_ID"
```

Progress reports the current phase and feature counts. Cancel with `POST .../jobs/{jobId}/cancel`; list active jobs with `GET .../jobs`.

## Verify

Once the job status is `Completed`, the auto-published layer is live:

```bash
curl "$HONUA_URL/ogc/features/collections"
```

```json
{"collections": [{"id": "…", "title": "parcels", …}], …}
```

## Authenticated sources

For secured ArcGIS services, send a `credentials` object instead of embedding credentials in the URL. Discovery accepts inline tokens; queued imports must use secret references — plaintext `accessToken`/`password` values are rejected so job state never persists secrets:

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "serviceUrl": "https://example.com/arcgis/rest/services/Private/FeatureServer",
    "layerId": 0,
    "tableName": "private_parcels",
    "credentials": {"mode": "token", "accessTokenSecretReference": "env:ARCGIS_PRIVATE_TOKEN"}
  }' \
  "$HONUA_URL/api/v1/admin/import/geoservices/start"
```

Supported modes are `token`, `oauth`, and `basic` (`username` + `passwordSecretReference`).

## Troubleshoot

- **`ServiceUrl is required` or URL validation errors (400)** — use the HTTPS service root ending in `FeatureServer`/`MapServer`; embedded credentials and private/loopback addresses are rejected.
- **`Failed to connect to ArcGIS service` (502)** — the source is unreachable or timing out; raise `timeoutSeconds` on discovery or `requestTimeoutSeconds` on the import.
- **`Distributed import coordination is unavailable. Retry when Redis is healthy.` (503)** — queued GeoServices imports need the Redis-backed job manager; check Redis connectivity.
- **Plaintext credential rejected on start** — queued jobs only accept `accessTokenSecretReference`/`passwordSecretReference`; store the secret and reference it.
- **Import completed but the layer is missing** — confirm `autoPublish` was not set to `false`; otherwise publish the imported table manually ([Publish layers](publish-layers.md)).

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md) — full migration workflow with inventory scanning.
- [ArcGIS inventory discovery](../migrate/from-arcgis-server.md) — pre-import compatibility scans.
- [Publish tiles](publish-tiles.md) — serve the imported layer as vector tiles.
