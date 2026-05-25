# Share Export And Traffic API

Honua Server owns scheduled Share export definitions and run history under the v1 admin API:

- `GET/POST /api/v1/admin/share/exports`
- `GET/PUT/DELETE /api/v1/admin/share/exports/{exportId}`
- `POST /api/v1/admin/share/exports/{exportId}/trigger`
- `POST /api/v1/admin/share/exports/{exportId}/pause`
- `POST /api/v1/admin/share/exports/{exportId}/resume`
- `GET /api/v1/admin/share/exports/{exportId}/runs`
- `GET /api/v1/admin/share/exports/{exportId}/runs/{runId}`

Definitions are anchored by `resourceId` when Console has a stable content/resource id. `serviceName` and `layerId` remain required compatibility locators for layer-backed resources.

`destinationType` supports `S3`, `Sftp`, `Webhook`, and `AuditSnapshot`. Responses include `destinationStatus`:

- `Supported`: a worker is registered and manual trigger can create an Operate job.
- `Unsupported`: the destination is modeled but no worker is registered in this build.
- `NotConfigured`: the destination is known but required environment credentials/configuration are missing.

Definitions may be stored with `Unsupported` or `NotConfigured` so Console can list and badge them. Triggering those definitions returns `422` with a problem title of `share-export-destination-unsupported` or `share-export-destination-not-configured`, and records a failed run with `jobRunId: null`.

When a trigger is backed by the job runner, the run response includes `jobRunId`, equal to `ExecutionJob.OperationId`. Console can deep-link to `/operate/jobs/{jobRunId}`.

`destinationConfig` must contain display-safe settings and secret references only. Raw secret-shaped keys such as passwords, tokens, private keys, API keys, and access keys are rejected unless represented as `secretRef`, `credentialRef`, or another `*Ref`/`*Reference` key.

Traffic reads are available at:

- `GET /api/v1/admin/share/traffic`
- `GET /api/v1/admin/share/traffic/series`
- `GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic`
- `GET /api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series`

Traffic queries accept `periodStart` and `periodEnd` ISO-8601 timestamps. Series queries also accept `bucketMinutes`. The read projection returns zero-valued summaries and contiguous series buckets when no telemetry has been collected yet.
