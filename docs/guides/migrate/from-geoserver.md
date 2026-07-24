# Migrate from GeoServer

Use `honua-migrate` to scan a GeoServer catalog, complete a server-validated dry-run plan, apply the reviewed catalog with explicit acknowledgement, and monitor the resulting Honua job.

**Prerequisites:** a running Honua server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), the [`honua-migrate`](https://github.com/honua-io/honua-migrate) CLI, Redis-backed jobs, and GeoServer REST credentials.

Set the local Honua credential and source details:

```bash
export HONUA_URL=https://honua.example.com
export HONUA_API_KEY=your-admin-api-key
export GEOSERVER_URL=https://geoserver.example.com/geoserver/rest
```

The CLI resolves `env:HONUA_API_KEY` locally for Honua authentication. The GeoServer password reference is interpreted by Honua, so its environment must contain the referenced variable.

## 1. Assess with a read-only catalog scan

```bash
honua-migrate services geoserver scan \
  --honua-url "$HONUA_URL" \
  --honua-api-key-ref env:HONUA_API_KEY \
  --geoserver-url "$GEOSERVER_URL" \
  --username admin \
  --geoserver-password-ref env:GEOSERVER_PASSWORD \
  --include-styles \
  --output geoserver-inventory.json
```

`scan` discovers workspaces, datastores, layers, styles, endpoints, and CRS information without mutating the source or target. Review unsupported stores, disabled layers, external graphics, and lossy style conversions before planning.

## 2. Complete a dry-run plan

```bash
honua-migrate services geoserver plan \
  --honua-url "$HONUA_URL" \
  --honua-api-key-ref env:HONUA_API_KEY \
  --geoserver-url "$GEOSERVER_URL" \
  --username admin \
  --geoserver-password-ref env:GEOSERVER_PASSWORD \
  --import-styles \
  --auto-publish \
  --unsupported-datastore log-warning \
  --unsupported-layer log-warning \
  --unsupported-style log-warning \
  --output geoserver-plan.json

honua-migrate plan verify geoserver-plan.json
```

`plan` starts a dry run, waits for its successful completion, and records the server evidence in a shared v1 plan contract. It does not apply catalog changes. Bound the scope with repeatable `--workspace`, `--datastore`, and `--layer` options; use `--workspace-map old=new` for renamed workspaces.

Review the classifications (`applied`, `already-applied`, `manual-review`, and `unsupported`), mappings, style diagnostics, overwrite policy, target SRID, auto-publish choice, batch size, and default workspace. Existing output files are rejected unless you intentionally pass `--force`.

GeoServer catalog apply does not copy feature data. Load required source tables into the target PostGIS database through your approved data-transfer process before apply; missing tables remain manual-review items.

## 3. Apply the reviewed plan

```bash
honua-migrate services geoserver apply \
  --honua-url "$HONUA_URL" \
  --honua-api-key-ref env:HONUA_API_KEY \
  --plan geoserver-plan.json \
  --geoserver-password-ref env:GEOSERVER_PASSWORD \
  --acknowledge-apply \
  --output geoserver-apply.json
```

Before mutation, the CLI verifies the plan digest and confirms that the recorded dry-run job still matches Honua. The secret reference is supplied again at apply time and is not stored in the plan. The mutating start request is never automatically retried. Record the returned job ID.

## 4. Monitor the job

```bash
export JOB_ID=the-job-id-from-apply

honua-migrate services geoserver status \
  --honua-url "$HONUA_URL" \
  --honua-api-key-ref env:HONUA_API_KEY \
  "$JOB_ID" \
  --output geoserver-status.json

honua-migrate services geoserver resume \
  --honua-url "$HONUA_URL" \
  --honua-api-key-ref env:HONUA_API_KEY \
  "$JOB_ID" \
  --wait-timeout 600 \
  --output geoserver-terminal.json
```

`status`, `list`, and `resume` are read-only. `resume` polls only the existing job and never creates a replacement. List all jobs with `honua-migrate services geoserver list` and cancel only with the explicit `--acknowledge-cancel` flag.

## 5. Reconcile and repoint clients

Verify the published target through the supported Honua CLI:

```bash
export HONUA_BASE_URL="$HONUA_URL"
honua services
honua layers my-service
honua query my-service/0 --count
honua query my-service/0 --limit 5 --format geojson
```

Compare counts, schemas, extents, representative records, relationships, and metadata with GeoServer or a source-native desktop client. Repoint WFS clients to `$HONUA_URL/wfs`, WMS clients to `$HONUA_URL/ogc/services/my-service/wms`, and WMTS clients to `$HONUA_URL/ogc/services/my-service/wmts` after validation.

The runtime-neutral `honua-migrate reconcile compare` command requires a durable `MigrationRun` and portable source/target snapshots. The GeoServer service adapter does not yet emit that bundle, so automated reconciliation is deferred until the adapter is wired. This guide intentionally avoids presenting an unsupported command sequence.

## Troubleshoot

- **A secret reference is rejected** — local Honua authentication requires `env:VARIABLE_NAME`; the GeoServer reference must also use supported server-side secret-reference syntax.
- **The dry run does not complete** — inspect the existing job with `status` or bounded `resume`; do not start another plan until its outcome is understood.
- **Apply refuses the plan** — the plan must contain completed dry-run evidence and an intact canonical digest.
- **Apply reports `manual-review` for a layer** — copy the backing table into target PostGIS, then create and review a new dry-run plan.
- **A style is lossy** — review the diagnostics and the [SLD migration reference](../style/import-sld-styles.md) before accepting the converted style.

More help: [deployment troubleshooting](../deploy/troubleshooting.md).
