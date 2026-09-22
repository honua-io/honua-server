---
type: guide
title: "Import from ArcGIS services"
description: "You'll have a layer from a public ArcGIS REST service copied into PostGIS and published in about 10 minutes."
resource: "honua://capability/import.geoservices"
---
# Import from ArcGIS services

You'll have a layer from a public ArcGIS REST service copied into PostGIS and published in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)). Queued imports require the distributed job queue (Redis) to be healthy.

The GeoServices import API discovers layers on a live ArcGIS Server / ArcGIS Online service, pages features into a PostGIS table, and auto-publishes the result as a Honua layer. This guide covers a simple one-service import; for full migrations (inventory scans, styles, batches) see [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md).

Source relationship declarations are retained during discovery, including their
related table IDs, key fields, roles, cardinality and composite ownership flag.
A single-layer import does not apply those cross-resource bindings. If the source
declares relationships, its row transfer can succeed while the job reports
`NeedsReview` with `fidelity.relationship.omitted` differences. Import the related
resources and use a reviewed relationship manifest with the published target IDs;
verify related-record queries before treating the dependency migration as complete.
Composite ownership requires a separate behavior review. A name match or a complete
row count is not proof that a relationship was recreated.

> Prefer an SDK? The same endpoints are wrapped by `honua-sdk-js` and `honua-sdk-dotnet`. Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Discover the service

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/import/geoservices/discover` with this body:

```json
{
  "serviceUrl": "https://services.arcgis.com/example/arcgis/rest/services/Parcels/FeatureServer",
  "timeoutSeconds": 30
}
```

Use an HTTPS service root URL ending in `FeatureServer` or `MapServer` (layer URLs are rejected). The response lists each layer's `id`, `name`, `geometryType`, and `featureCount`.

### 2. Start the import job

Run `POST /api/v1/admin/import/geoservices/start` with this body:

```json
{
  "serviceUrl": "https://services.arcgis.com/example/arcgis/rest/services/Parcels/FeatureServer",
  "layerId": 0,
  "tableName": "parcels",
  "targetSrid": 4326,
  "overwriteExisting": true,
  "autoPublish": true
}
```

Returns `202 Accepted` with a `jobId`. Optional fields: `whereClause` and `outputFields` to filter what is copied, `targetSchema`, `batchSize`, `serviceName` (target Honua service for auto-publishing; `autoPublish` defaults to `true`).

### 3. Poll the job

Run `GET /api/v1/admin/import/geoservices/jobs/{jobId}`, substituting the id returned in step 2.

Progress reports the current phase and feature counts. Cancel with `POST .../jobs/{jobId}/cancel`; list active jobs with `GET .../jobs`.

### Replace, cancel and retry

- With `overwriteExisting: true`, an existing table stays readable while the job runs and is replaced only when the transfer finishes. If any record fails to load, or the job is cancelled or interrupted, the existing table is left unchanged.
- Without `overwriteExisting`, the job fails when the table already exists.
- Only one job can import into a table at a time. A second job for the same table fails without changing it; retry once the first finishes.
- If the source gains or loses records while the job runs, the job reports `needs-review` with a `fidelity.source.changed-during-transfer` difference. Re-run it once the source is quiet. See [fidelity verdicts](../migrate/from-arcgis-server.md#replacements-retries-and-live-sources) for the full semantics.

## Verify

Once the job status is `Completed`, the auto-published layer is live:

Open `http://localhost:8080/ogc/features/collections` in a browser and confirm the imported collection is present:

```json
{"collections": [{"id": "…", "title": "parcels", …}], …}
```

## Authenticated sources

For secured ArcGIS services, send a `credentials` object instead of embedding credentials in the URL. Discovery accepts inline tokens; queued imports must use secret references — plaintext `accessToken`/`password` values are rejected so job state never persists secrets:

For a token stored in an environment-backed secret, run `POST /api/v1/admin/import/geoservices/start` with this body:

```json
{
  "serviceUrl": "https://example.com/arcgis/rest/services/Private/FeatureServer",
  "layerId": 0,
  "tableName": "private_parcels",
  "credentials": {
    "mode": "token",
    "accessTokenSecretReference": "env:ARCGIS_PRIVATE_TOKEN"
  }
}
```

Supported modes are `token`, `oauth`, and `basic` (`username` + `passwordSecretReference`).

## Troubleshoot

- **`ServiceUrl is required` or URL validation errors (400)** — use the HTTPS service root ending in `FeatureServer`/`MapServer`; embedded credentials and private/loopback addresses are rejected.
- **`Failed to connect to ArcGIS service` (502)** — the source is unreachable or timing out; raise `timeoutSeconds` on discovery or `requestTimeoutSeconds` on the import.
- **ArcGIS authentication required/expired (401) or denied (403)** — supply or refresh source credentials; discovery preserves these statuses and does not misreport them as a connectivity failure.
- **`Distributed import coordination is unavailable. Retry when Redis is healthy.` (503)** — queued GeoServices imports need the Redis-backed job manager; check Redis connectivity.
- **Plaintext credential rejected on start** — queued jobs only accept `accessTokenSecretReference`/`passwordSecretReference`; store the secret and reference it.
- **Secret reference refused (400) or not resolved** — a reference must be a whole `provider:identifier` value permitted under `Security__RequestSecretReferences__*` ([References supplied in a request](../deploy/configuration.md#references-supplied-in-a-request)). Nothing is permitted by default.
- **Import completed but the layer is missing** — confirm `autoPublish` was not set to `false`; otherwise publish the imported table manually ([Publish layers](publish-layers.md)).

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Migrate from ArcGIS Server](../migrate/from-arcgis-server.md) — full migration workflow with inventory scanning.
- [ArcGIS inventory discovery](../migrate/from-arcgis-server.md) — pre-import compatibility scans.
- [Publish tiles](publish-tiles.md) — serve the imported layer as vector tiles.
