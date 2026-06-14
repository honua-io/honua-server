# Migrate from GeoServer

You'll scan a GeoServer catalog over its REST API, validate the import with a dry run, apply the catalog to Honua, convert SLD styles, and repoint WMS/WFS clients.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), a healthy Redis-backed job queue, and GeoServer REST credentials for the source.

The importer is staged and conservative: the scan is discovery-only, the dry run reports what would happen, and the apply step is gated behind an explicit flag. Apply publishes catalog entries (workspaces, layer groups, PostGIS-backed layers) but does not copy feature data — a layer is published only when its source PostGIS table already exists in the target Honua database, and everything else is recorded as manual-review or unsupported evidence.

## Steps

### 1. Scan the GeoServer catalog

```bash
HONUA_URL=http://localhost:8080
HONUA_API_KEY=your-admin-api-key
GEOSERVER=https://geoserver.example.com/geoserver/rest
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d "{\"sourceKind\":\"geoserver\",\"sourceUrl\":\"$GEOSERVER\",\"username\":\"admin\",\"password\":\"geoserver\",\"includeStyleContent\":true}" \
  "$HONUA_URL/api/v1/admin/import/scan?export=json" -o geoserver-inventory.json
```

The scan returns a deterministic inventory artifact — workspaces, layers, datastores, styles (linked by URL, never echoed inline), service endpoints, and CRS details — without touching the source. Basic auth is used only when both `username` and `password` are supplied; supplying one falls back to anonymous discovery and notes it in `authPosture.notes`. `includeStyleContent: true` additionally inspects SLD documents for conversion warnings and external graphics.

### 2. Review compatibility

```bash
jq '{status: .scanCompleteness.status, overall: .overallCompatibility, summary: .summary}' geoserver-inventory.json
```

Check `scanCompleteness.status` and `overallCompatibility` before planning anything, then walk per-item codes such as `GEOSERVER_SUPPORTED`, `GEOSERVER_MANUAL_REVIEW`, `GEOSERVER_UNSUPPORTED_STORE`, `GEOSERVER_DISABLED_LAYER`, `GEOSERVER_STYLE_CONVERSION_REQUIRED`, and `GEOSERVER_EXTERNAL_GRAPHIC` across `resources`, `styles`, and `externalDependencies`. Layers backed by PostGIS datastores migrate with the highest fidelity.

### 3. Dry-run the import

```bash
export GEOSERVER_PASSWORD=geoserver
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d "{\"geoServerRestUrl\":\"$GEOSERVER\",\"username\":\"admin\",\"passwordSecretReference\":\"env:GEOSERVER_PASSWORD\",\"dryRun\":true}" \
  "$HONUA_URL/api/v1/admin/import/geoserver/start"
```

Returns `202 Accepted` with a `jobId`; the dry run validates connectivity and reports what would be imported without changing anything. Queued jobs reject plaintext `password`/`honuaApiKey` values — use `passwordSecretReference` (for example `env:GEOSERVER_PASSWORD`). Bound the run with optional `workspaceNames`, `dataStoreNames`, or `layerNames` arrays.

### 4. Apply the bounded catalog import

```bash
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d "{\"geoServerRestUrl\":\"$GEOSERVER\",\"username\":\"admin\",\"passwordSecretReference\":\"env:GEOSERVER_PASSWORD\",\"dryRun\":false,\"applyMode\":true}" \
  "$HONUA_URL/api/v1/admin/import/geoserver/start"
```

Non-dry-run requests without `"applyMode": true` are rejected with a 400 safety-gate error so applying the reviewed plan is always an explicit decision. The apply emits a deterministic apply plan with ordered steps plus a per-step execution record; catalog writes are idempotent (`INSERT ... ON CONFLICT DO NOTHING`), so re-running the same import reports `already-applied` instead of duplicating entries. Copy feature data into the target PostGIS database yourself (for example with `ogr2ogr` or `pg_dump`/`pg_restore`) before or after apply — layers whose tables are missing are recorded as manual-review steps.

### 5. Monitor the job

```bash
JOB_ID=paste-jobid-from-step-4
curl -s -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/import/geoserver/jobs/$JOB_ID"
```

Completed apply jobs include `progress.applyPlan` and `progress.applyExecution` with per-step outcomes (`applied`, `already-applied`, `manual-review`, and unsupported records). List active jobs with `GET .../import/geoserver/jobs`; cancel with `POST .../import/geoserver/jobs/$JOB_ID/cancel`.

### 6. Convert SLD styles

```bash
LAYER_ID=1
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/xml" \
  --data-binary @style.sld \
  "$HONUA_URL/api/v1/admin/metadata/layers/$LAYER_ID/style/import-sld"
```

Honua's canonical style format is MapLibre GL Style JSON; the import endpoint converts an SLD/SE document server-side and returns the stored style plus a `diagnostics` array of lossy-conversion warnings. The supported SLD subset, diagnostic taxonomy, and export path are documented in the [SLD migration reference](../style/import-sld-styles.md).

### 7. Repoint WMS/WFS/WMTS clients

```bash
SERVICE_ID=my-service
curl -s "$HONUA_URL/wfs?service=WFS&request=GetCapabilities&version=2.0.0" | head -5
```

Honua serves the same classic OGC protocols GeoServer clients already speak, so cutover is a URL swap: WFS clients move to `$HONUA_URL/wfs`, WMS clients to `$HONUA_URL/ogc/services/$SERVICE_ID/wms` or `$HONUA_URL/rest/services/$SERVICE_ID/MapServer/WMS`, and WMTS clients to `$HONUA_URL/ogc/services/$SERVICE_ID/wmts` or `$HONUA_URL/rest/services/$SERVICE_ID/MapServer/WMTS`. Clients can also upgrade to OGC API Features (`/ogc/features`) or vector tiles at their own pace — see [protocols](../../concepts/protocols.md) for the full surface and version notes. Replace GeoServer credentials with Honua API keys or OIDC tokens ([authentication](../secure/authentication.md)).

## Verify

```bash
curl -s "$HONUA_URL/ogc/features/collections" | jq '.collections[].id'
```

Migrated layers should appear as collections, the WFS capabilities document from step 7 should list them as feature types, and a repointed client should render without request changes.

## Troubleshoot

- **400 `Non-dry-run GeoServer imports require applyMode=true`** — the safety gate fired; add `"applyMode": true` once the dry-run report has been reviewed.
- **400 `Password must be provided as passwordSecretReference`** — queued jobs never persist plaintext secrets; export the password and reference it as `env:GEOSERVER_PASSWORD`.
- **503 `Distributed import coordination is unavailable`** — queued imports need the Redis-backed job manager; restore Redis and retry.
- **Apply step reports `manual-review`: source table not found** — this slice does not copy data; load the table into the target PostGIS database, then re-run the apply (idempotent).
- **Scan shows `authPosture.mode = "anonymous-or-auth-required"`** — only one of `username`/`password` was supplied, so discovery ran anonymously; send both and rescan.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [SLD migration reference](../style/import-sld-styles.md) — supported SLD subset and diagnostics.
- [Protocols](../../concepts/protocols.md) — choose the right protocol per client after cutover.
- [Migrate from ArcGIS Server](from-arcgis-server.md) — the same staged workflow for Esri sources.
