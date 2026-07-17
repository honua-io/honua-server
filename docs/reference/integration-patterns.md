# Integration patterns

Cross-cutting patterns for building integrations against Honua: choosing a surface, discovering capabilities at runtime, paginating correctly, reacting to changes (polling vs webhooks), authenticating, and keeping batch loads idempotent. Protocol-specific request/response details live in the [protocol references](protocols/ogc-apis.md).

## Choosing a surface

| Pattern | Best protocol | Notes |
| --- | --- | --- |
| Interactive queries and CRUD | OGC API Features | Standards-compliant JSON/GeoJSON; CQL2 filtering. |
| Esri clients and tooling | GeoServices REST (`/rest/services`) | FeatureServer for data, MapServer for maps. |
| BI tools (Power BI, Excel) | OData v4 (`/odata`) | `$filter`, `$select`, `$count`. |
| Bulk/columnar analytics export | FeatureServer `f=parquet` / `f=arrow` | GeoParquet and Arrow IPC, PostGIS-backed layers. |
| High-throughput service-to-service | gRPC `geospatial.v1` | Streaming queries and edits ([reference](protocols/grpc.md)). |
| Batch ingest | Admin import API | File/URL imports with jobs ([formats](data-formats.md)). |

## Runtime capability discovery

Fetch `GET /api/v1/capabilities/manifest` before rendering feature-specific controls instead of hardcoding assumptions. The manifest is public, request-scoped, and `no-store`; it reports package families, temporal/sync/realtime/jobs/transport availability, runtime limits, and reason codes for the current tenant and principal. Treat it as discovery only — authorization still happens at the operation endpoint. Contract and stable ids: [admin API overview](admin-api/overview.md).

## Authentication patterns

- **API keys** — send `X-API-Key` on admin and protected data requests; manage keys via `/api/v1/admin/api-keys` (create, rotate, revoke). Scope keys to least privilege and rotate on a schedule.
- **OIDC** — configure an identity provider for interactive users; see [authentication guide](../guides/secure/authentication.md).
- **ArcGIS clients** — the opt-in Portal OAuth2 bridge (`/sharing/rest/oauth2/*`) brokers ArcGIS named-user sign-in to your OIDC provider; register every redirect URI explicitly.
- **mTLS** — client-certificate authentication for native/admin surfaces; see [TLS and mTLS](../guides/secure/tls-and-mtls.md).

## Pagination and result completeness

- Query endpoints apply `maxRecordCount` (default 2 000 per the shipped templates); page with `resultOffset`/`resultRecordCount` (GeoServices) or `limit` + `next` links (OGC API Features).
- JSON responses set `exceededTransferLimit` when truncated; **binary formats (GeoParquet, Arrow, FlatGeobuf) carry no truncation flag** — compare the returned row count against `maxRecordCount`, or call `returnCountOnly=true` first.
- For full-table exports prefer the async export job surface (`.../layers/{layerId}/export`) or paged `f=parquet` pulls.

## Reacting to changes: polling vs webhooks

**Polling** — re-query on an interval with an indexed change filter (e.g. an `updated_at` column) and page through results. Simple, works everywhere, but latency equals the poll interval and wide intervals miss intermediate states.

**Webhooks** — enable outbound feature-change webhooks and let Honua push edits to you:

| Variable | Purpose |
| --- | --- |
| `FeatureChangeEvents__Webhook__Enabled` | Enable delivery. |
| `FeatureChangeEvents__Webhook__Url` | Absolute target URL. |
| `FeatureChangeEvents__Webhook__Secret` | Shared HMAC secret — verify the signature on every delivery. |
| `FeatureChangeEvents__Webhook__MaxAttempts` | Delivery attempts per event (default 5, exponential backoff). |

Keep the webhook handler thin: verify the signature, enqueue, return fast — process from the queue so delivery retries do not pile up behind slow handlers. Deliveries can arrive more than once; key processing on the event id.

## ETL and idempotent loads

Extract with OData or OGC API Features, transform to GeoJSON, load through FeatureServer edits:

```python
import requests

resp = requests.post(
    "http://localhost:8080/rest/services/1/FeatureServer/0/addFeatures",
    json={"features": features},
    headers={"X-API-Key": API_KEY},
)
resp.raise_for_status()
```

- Make loads idempotent: key features on a stable source identifier and update-or-insert rather than blind-appending, so a re-run after a partial failure converges instead of duplicating.
- Respect edit limits (`Limits__Edits__MaxFeaturesPerEdit`, default 500 per operation) and batch accordingly.
- For columnar hand-off to analytics stacks, export with `f=parquet` (GeoParquet 1.1.0, WKB geometry) or `f=arrow` (Arrow IPC with `geoarrow.wkb` metadata) and read directly into pandas/GeoPandas, DuckDB, or Polars. GeoParquet output honours `outSR` for any CRS with a resolvable PROJJSON definition (EPSG:4326 emits the OGC:CRS84 default; other supported SRIDs carry an authoritative PROJJSON `crs` in the `geo` metadata); an `outSR` without a resolvable definition returns an error. GeoArrow output is EPSG:4326 only.
- Orchestrate with your existing platform (Airflow, Dagster, Prefect); server-side geoprocessing sources/transforms/sinks can also run pipelines as jobs ([geoprocessing operations](geoprocessing-operations.md)).

## Related pages

- [Query features guide](../guides/query-analyze/query-features.md)
- [Export data guide](../guides/query-analyze/export-data.md)
- [Admin API overview](admin-api/overview.md)
- [Data formats](data-formats.md)
