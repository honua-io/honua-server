---
type: guide
title: "Migrate from ArcGIS Server"
description: "Use honua-migrate to discover an ArcGIS FeatureServer or MapServer, create a reviewable plan, apply it with explicit acknowledgement, and monitor the resulting Honua job."
---
# Migrate from ArcGIS Server

Use `honua-migrate` to discover an ArcGIS FeatureServer or MapServer, create a reviewable plan, apply it with explicit acknowledgement, and monitor the resulting Honua job. The service workflow is deliberately staged so discovery and planning cannot mutate either system.

**Prerequisites:** a running Honua server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), the [`honua-migrate`](https://github.com/honua-io/honua-migrate) CLI, and an ArcGIS service root ending in `FeatureServer` or `MapServer`.

Set the Honua connection once. The CLI reads these values without writing them to artifacts:

```bash
export HONUA_URL=https://honua.example.com
export HONUA_API_KEY=your-admin-api-key
export ARCGIS_SERVICE=https://gis.example.com/arcgis/rest/services/Public/Parcels/FeatureServer
```

## 1. Assess with read-only discovery

```bash
honua-migrate services arcgis discover "$ARCGIS_SERVICE" \
  --output arcgis-discovery.json
```

`discover` reads the source and emits a versioned, credential-free artifact. For a secured source, pass a server-side secret reference such as `--token-secret-ref env:ARCGIS_TOKEN`; the secret value must be available to Honua, and the reference is not retained in the artifact.

Review the discovered layers, fields, spatial references, relationships, styles, attachments, and unsupported capabilities before planning. The current command works on one service root at a time; organization-wide Portal content assessment remains a separate inventory workflow.

## 2. Create and verify a plan

Create one plan per source layer:

```bash
honua-migrate services arcgis plan "$ARCGIS_SERVICE" \
  --layer-id 0 \
  --table-name parcels \
  --service-name parcels \
  --target-srid 4326 \
  --output parcels-plan.json

honua-migrate plan verify parcels-plan.json
```

Planning is local and does not contact either server. The plan is a shared v1 migration contract with a canonical digest. Review table and service names, the target SRID, overwrite behavior, query filters, output fields, and auto-publish behavior before applying it. Existing output files are rejected unless you intentionally pass `--force`.

The current ArcGIS service command creates a single-layer plan. A multi-layer batch command is not exposed yet; keep multiple plans under the same change record and apply them in dependency order.

## 3. Apply the reviewed plan

```bash
honua-migrate services arcgis apply parcels-plan.json \
  --yes \
  --output parcels-apply.json
```

`--yes` is required before the CLI resolves credentials, reads the plan, or contacts Honua. For a secured source, repeat the server-side token reference with `--token-secret-ref env:ARCGIS_TOKEN`. The command never retries the mutation automatically. Record the returned job ID from `parcels-apply.json`.

## 4. Monitor the job

```bash
export JOB_ID=the-job-id-from-apply

honua-migrate services arcgis status "$JOB_ID" --output parcels-status.json
honua-migrate services arcgis resume "$JOB_ID" \
  --max-wait 600 \
  --output parcels-terminal.json
honua-migrate services arcgis list --output arcgis-jobs.json
```

`status`, `list`, and `resume` are read-only. `resume` only polls the existing job and never requeues it. Cancellation is a separate mutation and requires acknowledgement:

```bash
honua-migrate services arcgis cancel "$JOB_ID" --yes
```

## 5. Reconcile the result

Use the supported Honua data-plane CLI to inspect the published target:

```bash
export HONUA_BASE_URL="$HONUA_URL"
honua services
honua layers parcels
honua query parcels/0 --count
honua query parcels/0 --limit 5 --format geojson
```

Compare those results with the source inventory and ArcGIS Pro or another source-native client. Check feature counts, schemas, extents, representative records, relationships, and metadata before cutover.

### Read the fidelity verdict

Every import job carries a `fidelityVerdict` alongside its status. The status says whether the job
finished; the verdict says whether the migrated layer is equivalent to the source. Read the verdict,
not the status, before you cut over:

| Verdict | Meaning | What to do |
|---|---|---|
| `full-fidelity` | Every required check ran and passed. | Safe to proceed to cutover. |
| `unverified` | Nothing blocking was found, but at least one required check did not run. | Treat parity as unproven. Check `fidelityDifferences` for which check was skipped and verify that construct by hand. |
| `incomplete` | A blocking difference was found: dropped records, missing attachments, an omitted relationship, or a schema/metadata mismatch between source and target. | The job reports `needs-review` and is not a successful migration. Fix the cause and re-import. |

`fidelityDifferences` lists the actionable per-resource findings behind the verdict, each with a
stable `code`, the affected `subject`, and the `expected` and `actual` values. Attachments and
relationships are reconciled on their own evidence, so a layer whose feature counts match can still
be `incomplete` because its attachments or relationship classes did not survive the import.

A skipped check is never treated as a passing check. An import run on a deployment without the
catalog read-back seam, for example, completes as `unverified` rather than `full-fidelity`, because
counts can match while the schema is wrong.

The runtime-neutral `honua-migrate reconcile compare` command exists for a durable `MigrationRun` plus portable source and target snapshots. The ArcGIS service adapter does not yet emit that run/snapshot bundle, so this guide does not present a command that would fail. Automated ArcGIS reconciliation remains deferred until that adapter is wired.

## 6. Repoint clients

Existing Esri clients keep their workflow and replace only the service URL. Desktop setup is covered in [connect ArcGIS Pro](../connect/arcgis-pro.md); web and SDK migrations are covered in [ArcGIS apps and SDKs](arcgis-apps-and-sdks.md). ArcGIS Pro and ArcGIS Maps SDK licensing remains an Esri client concern; Honua replaces the server, not client licensing.

## Cutover checklist

Record an explicit state (`pass`, `fail`, `unknown`, or `not-applicable`) and evidence for each item:

| Item | Evidence required |
|---|---|
| Inventory confirmed | Source owner reviewed discovery scope and authentication posture. |
| Plans reviewed | Target names, filters, styles, unsupported items, and overwrite choices were approved. |
| Jobs completed | Every apply job reached a terminal state and its artifact was retained. |
| Parity reviewed | Counts, schemas, extents, samples, relationships, and metadata were compared. |
| Known gaps accepted | Every failed or unknown check has a remediation, waiver, or deferral. |
| Rollback prepared | Database restore point, traffic switchback, cache purge, escalation contact, and decision owner are documented. |

Run a pilot subset first. Move production traffic only when the latest evidence has no unaccepted failures.

## Troubleshoot

- **The service URL is rejected** — use a credential-free HTTP(S) service root ending in `FeatureServer` or `MapServer`; layer URLs, URL userinfo, query strings, and fragments are rejected.
- **The source needs a token** — configure the secret in the Honua environment and pass only its `env:VARIABLE_NAME` reference.
- **Apply refuses to run** — verify the plan digest, ensure the output path does not already exist, and add `--yes` only after review.
- **A job is still running** — use `resume` with a bounded `--max-wait`; it monitors the existing job without replaying the import.
- **Counts match but a client operation fails** — check the operation in the [GeoServices parity reference](../../reference/compatibility/geoservices-parity.md).

More help: [deployment troubleshooting](../deploy/troubleshooting.md).
