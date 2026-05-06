# Mobile Offline Demo Fixture

Issue: honua-io/honua-server#895

This fixture gives mobile and SDK agents a deterministic Honua server dataset for SDK-backed offline field operations. Server ownership is limited to fixture data, catalog metadata, FeatureServer API behavior, and documented setup/cleanup. Mobile owns GeoPackage/native runtime packaging. The .NET SDK owns portable offline contracts and sync-engine behavior.

## Fixture Contract

- Service: `mobile_offline_demo`
- Editable layer: `68910` / `Offline Field Sites`
- Readonly context layer: `68920` / `Offline Work Zones`
- Baseline seed: `tests/seed/mobile-offline-demo-v1.sql`
- Conflict delta: `tests/seed/mobile-offline-demo-conflict-delta.sql`
- Package id: `mobile-offline-field-ops-v1`
- Extent: `[-158.1250, 21.2600, -157.7000, 21.5200]`

The service metadata stores a provisional offline manifest under `metadata.demoFixture.offlinePackageManifest`. The layer metadata stores form/schema hints under `metadata.form` and per-layer offline hints under `metadata.offline`. The fixture is intentionally public and sets `accessPolicy.allowAnonymousWrite=true` so disconnected edit, replica, and conflict harnesses can run against local and staging demo stacks without cloud-only credentials.

## Local Run

```bash
scripts/demos/run-mobile-offline-demo.sh baseline
```

For conflict testing:

```bash
scripts/demos/run-mobile-offline-demo.sh conflict-after-download
```

The conflict scenario pauses after the baseline seed is applied. Download the offline package or create the replica from the mobile harness, then press Enter. The script applies `mobile-offline-demo-conflict-delta.sql`, which advances feature `6891002` from `sync_version = 1` to `sync_version = 2`.

The local demo maps REST/gRPC-Web to `http://localhost:18081` and native h2c gRPC to
`http://localhost:18082`. When running `honua-mobile` live tests against this demo, set
`HONUA_MOBILE_LIVE_SERVER_BASE_URL=http://localhost:18081` and
`HONUA_MOBILE_LIVE_SERVER_GRPC_URL=http://localhost:18082`.

Cleanup uses the same isolated docker-compose pattern as the STAC ops demo:

```bash
COMPOSE_PROJECT_NAME=honua-mobile-offline-demo HONUA_HTTP_PORT=18081 HONUA_GRPC_PORT=18082 POSTGRES_PORT=55433 docker compose -f docker-compose.yml --project-directory . down --remove-orphans --volumes
```

## Cloud Or Staging Provisioning

Apply the baseline seed to the target Honua Postgres database:

```bash
psql "$HONUA_DATABASE_URL" -v ON_ERROR_STOP=1 -f tests/seed/mobile-offline-demo-v1.sql
```

Apply the conflict delta only after the mobile harness has downloaded the baseline package:

```bash
psql "$HONUA_DATABASE_URL" -v ON_ERROR_STOP=1 -f tests/seed/mobile-offline-demo-conflict-delta.sql
```

Rerunning the baseline seed resets the fixture. The seed deletes only `mobile_offline_demo`, layers `68910`/`68920`, and the fixture-owned feature IDs before reinserting the baseline state. It also applies idempotent catalog-column compatibility DDL for older demo images before inserting rows, aligning them with the canonical provider-binding columns used by current migrations.

## API Smoke Paths

```text
GET /rest/services/mobile_offline_demo/FeatureServer?f=json
GET /rest/services/mobile_offline_demo/FeatureServer/68910?f=json
GET /rest/services/mobile_offline_demo/FeatureServer/68910/query?where=1%3D1&outFields=*&returnGeometry=true&f=json
POST /rest/services/mobile_offline_demo/FeatureServer/createReplica
```

Suggested `createReplica` form payload:

```text
replicaName=mobile-offline-field-ops-v1
layers=68910,68920
syncModel=perLayer
dataFormat=json
returnAttachments=false
f=json
```

## Cache Policy

The fixture metadata declares:

- metadata TTL: `300` seconds
- manifest TTL: `300` seconds
- feature page TTL: `0` seconds

FeatureServer service and layer metadata endpoints use the existing `ServiceMetadata` and `LayerMetadata` output-cache policies with ETag support. Query pages should be treated as live pull data by offline harnesses unless a generated offline package artifact is introduced.

## Known Contract Gap

There is not yet a server-native portable offline package manifest endpoint distinct from FeatureServer metadata and `createReplica`. Until that contract lands, clients should read the provisional manifest from service metadata and use FeatureServer query/createReplica paths. The remaining server/API contract to define is:

- a stable manifest route and response shape for `packageId`, source layer IDs, bbox, field schema, page size, cache policy, and conflict token fields;
- whether optimistic conflict rejection is enforced server-side on `sync_version`/generation mismatch or detected by the SDK sync engine after pulling server state.
