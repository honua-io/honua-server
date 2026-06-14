# Migrate from ArcGIS Server

You'll inventory an ArcGIS Server or ArcGIS Online source, bulk-import its layers into PostGIS as published Honua services, verify parity, and cut traffic over.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a healthy Redis-backed job queue for imports. Migrating a single service? Use the shorter [import from ArcGIS services](../publish/import-from-arcgis-services.md) guide instead.

The migration workflow is deliberately staged: a read-only scan produces a deterministic inventory artifact you review before anything is imported, a batch run copies layers into PostGIS and auto-publishes them, and cutover only happens after you have compared the result against the source.

## Steps

### 1. Scan the source inventory

```bash
HONUA_URL=http://localhost:8080
HONUA_API_KEY=your-admin-api-key
SOURCE=https://services.arcgis.com/example/arcgis/rest/services/Parcels/FeatureServer
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d "{\"sourceKind\":\"arcgis-geoservices-rest\",\"sourceUrl\":\"$SOURCE\",\"timeoutSeconds\":30}" \
  "$HONUA_URL/api/v1/admin/import/scan?export=json" -o parcels-inventory.json
```

The scan is read-only and safe to rerun; `?export=json` writes an indented artifact you can commit to a migration repo. Run one scan per HTTPS service root ending in `FeatureServer` or `MapServer` (layer URLs are rejected); for an org-wide ArcGIS Online/Portal content inventory, use the `honua-migrate` CLI ([ArcGIS apps and SDKs](arcgis-apps-and-sdks.md)).

For secured sources add a `credentials` object — `{"mode": "token", "accessTokenSecretReference": "env:ARCGIS_TOKEN"}` (modes: `token`, `oauth`, `basic`). Optionally set `"artifactSet": "all"` to also receive a generated migration manifest and parity-evidence planning artifact in one envelope.

### 2. Review the compatibility report

```bash
jq '{status: .scanCompleteness.status, overall: .overallCompatibility, summary: .summary}' parcels-inventory.json
```

HTTP 200 only means the scanner returned an artifact — treat `scanCompleteness.status` and `overallCompatibility` as the actual result, then drill into per-item assessments in `resources`, `styles`, and `externalDependencies`. Every assessment carries a stable code:

| Code | Level | What to do |
|---|---|---|
| `COMPATIBLE` | compatible | Nothing — layer imports as-is. |
| `MANUAL_REVIEW` | partial | Recreate the renderer in Honua after data import. |
| `ARCGIS_EXTERNAL_SYMBOL` | partial | Mirror or replace external symbol assets. |
| `ARCGIS_ATTACHMENTS` | partial | Plan a separate attachment migration. |
| `ARCGIS_DOMAIN_TRUNCATED` | warning | Coded-value domain exceeded the capture cap; re-import it manually if needed. |
| `ARCGIS_QUERY_CAPABILITY_MISSING` | incompatible | Layer does not advertise `Query`; export its data another way. |
| `ARCGIS_UNSUPPORTED_GEOMETRY` / `ARCGIS_UNSUPPORTED_RENDERER` | incompatible | Normalize the geometry / rebuild the style manually. |
| `ARCGIS_TOKEN_REQUIRED` / `ARCGIS_TOKEN_EXPIRED` / `ARCGIS_ACCESS_DENIED` | partial | Supply (or rotate) credentials and rerun the scan. |

Honua does not refresh ArcGIS tokens — an expired token surfaces as `authPosture.mode = "expired-token"`; rotate the referenced secret and rerun. Resolve or explicitly waive every non-compatible item before importing.

### 3. Import in batches

```bash
curl -s -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d "{
    \"sourceKind\": \"arcgis-geoservices-rest\",
    \"sourceUrl\": \"$SOURCE\",
    \"layers\": [
      {\"sourceResourceId\": \"parcels-0\", \"serviceUrl\": \"$SOURCE\", \"layerId\": 0, \"tableName\": \"parcels\", \"serviceName\": \"parcels\"},
      {\"sourceResourceId\": \"zones-1\", \"serviceUrl\": \"$SOURCE\", \"layerId\": 1, \"tableName\": \"zones\", \"serviceName\": \"parcels\", \"dependsOn\": [\"parcels-0\"]}
    ]
  }" \
  "$HONUA_URL/api/v1/admin/import/migrations"
```

Returns `202 Accepted` with a `batchId`. A batch footprint holds up to 500 layers; each layer becomes an ordered child import job (`dependsOn` controls sequencing) that copies features into PostGIS and auto-publishes the layer. To apply manifest relationship classes after all layers publish, pass `manifestBody` plus `"applyRelationships": true`. Batches are resumable and advanced by a leader-elected background service; secured sources should be imported per-layer via [`/import/geoservices/start`](../publish/import-from-arcgis-services.md) with secret-reference credentials instead.

### 4. Track the batch

```bash
BATCH_ID=paste-batchid-from-step-3
curl -s -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/import/migrations/$BATCH_ID"
```

The rolled-up status is `running`, `succeeded`, `failed`, `cancelled`, or `needs-review`, with per-child `status`, `jobId`, `publishedLayerId`, and `statusNote`. Individual child jobs can be inspected or cancelled via `GET/POST $HONUA_URL/api/v1/admin/import/geoservices/jobs/{jobId}[/cancel]`.

### 5. Verify parity

```bash
SERVICE_ID=parcels
curl -s "$SOURCE/0/query?where=1%3D1&returnCountOnly=true&f=json"
curl -s "$HONUA_URL/rest/services/$SERVICE_ID/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"
```

Compare feature counts per layer, then spot-check attribute queries and extents the same way — the requests are identical on both servers because Honua serves the same GeoServices REST surface ([parity reference](../../reference/compatibility/geoservices-parity.md)). For an automated per-layer fidelity comparison, use the `honua-migrate reconcile` command ([ArcGIS apps and SDKs](arcgis-apps-and-sdks.md)).

### 6. Repoint clients

```bash
curl -s "$HONUA_URL/rest/services/$SERVICE_ID/FeatureServer?f=json" | jq .layers
```

Existing Esri clients keep their workflow and swap only the URL: desktop and SDK clients per [connect ArcGIS Pro](../connect/arcgis-pro.md), web apps per [ArcGIS apps and SDKs](arcgis-apps-and-sdks.md). Clients that sign in get tokens from Honua's `/sharing/rest/generateToken` endpoint automatically.

### 7. Work the cutover checklist

Before moving production traffic, record an explicit state (`pass`, `fail`, `unknown`, `not-applicable`) for each item, with evidence — a `pass` without a linked artifact, runbook, or signed approval does not count:

| Item | Evidence required |
|---|---|
| Inventory confirmed | Source owner reviewed scan scope, auth posture, and completeness warnings. |
| Manifest reviewed | Target resources, style actions, manual-review and unsupported items reviewed. |
| Parity report reviewed | Per-layer count/schema/query checks from step 5 reviewed after the pilot. |
| Known gaps accepted | Every `fail`/`unknown` item has an accepted remediation, waiver, or deferral. |
| Rollback plan documented | See below. |
| Traffic switch planned | DNS, load-balancer, client URL, and cache-warming changes scheduled. |

A rollback plan must name: the database restore point and its retention window, the DNS/load-balancer steps that return traffic to the source, required CDN/tile/client cache purges, the escalation contact per dependent team, the point-of-no-return and post-cutover validation window, and the single decision owner who authorizes rollback. Honua does not execute the rollback — keep it in your own runbook. Run a pilot subset through steps 3–6 first, and only cut over once the latest parity evidence has no unaccepted failures.

## Verify

```bash
curl -s "$HONUA_URL/rest/services?f=json" | jq .
```

Every migrated service should be listed, each layer's count probe from step 5 should match the source, and a repointed client should draw the layer and open its attribute table.

## Troubleshoot

- **Scan returns 200 but `scanCompleteness.status` is `failed`** — check `overallCompatibility.code`: `ARCGIS_TOKEN_REQUIRED`/`ARCGIS_ACCESS_DENIED` mean you need (working) credentials; rerun after adding them.
- **400 on `sourceUrl` or `serviceUrl`** — use the HTTPS service root ending in `FeatureServer`/`MapServer`; layer URLs, embedded credentials, and private/loopback addresses are rejected.
- **503 `Distributed import coordination is unavailable`** — batch and queued imports need the Redis-backed job manager; restore Redis and retry.
- **Batch ends `needs-review` or a child fails** — read the child's `statusNote`, fix the cause (source outage, table conflict, incompatible layer), and submit the remaining layers as a new batch; already-published children are not re-imported.
- **Counts match but a client operation fails** — Honua's GeoServices parity is broad but not total; check the operation in the [parity reference](../../reference/compatibility/geoservices-parity.md) before debugging the client.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [ArcGIS apps and SDKs](arcgis-apps-and-sdks.md) — repoint web apps with the compat layer and codemod.
- [Connect ArcGIS Pro](../connect/arcgis-pro.md) — desktop and Esri SDK clients against Honua.
- [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md) — operation-level compatibility detail.
