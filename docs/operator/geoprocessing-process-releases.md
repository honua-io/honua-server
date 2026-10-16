---
type: runbook
title: "GP process release bindings"
description: "Operate the per-process release binding: publish locks, activate a release under an expected binding version, roll back, and read what each job executed."
---
# GP process release bindings

A custom geoprocessing process runs the release its **binding** names. The binding maps the
logical process name to one published release lock (see the
[descriptor and lock reference](../reference/gp-process-release.md)), carries the provider
identities jobs are pinned to, and has a version that every activation increments. Activation
is a compare-and-set on that version, so two deployments cannot both believe they won.

This is the server half of the Git-based release flow. Deployment must verify artifact
availability and worker readiness before activating a binding. The release repository's
reference workflows require integration with these endpoints before they can report activation.

## Endpoints

All routes are under `/api/v1/admin/gp/process-releases` and require admin credentials.

| Method and path | Purpose |
|---|---|
| `POST /` | Publish a lock (the request body is the lock JSON). Idempotent by digest: `201` the first time, `200` afterwards with `created: false`. |
| `GET /?process=<name>` | List published releases, newest first. |
| `GET /{digest}` | The lock document as published. |
| `GET /bindings` | Every live binding plus the server's execution contract version and supported target kinds. |
| `GET /bindings/{process}` | The live binding of one process; `bindingVersion` is `0` and `lockDigest` is `null` before the first activation. This is what a deployment reads as `expectedBindingVersion`. |
| `POST /bindings/{process}/activate` | Activate a release. `200` with the new binding, or `409` with `reasons[]`, `details[]` and the live binding. |

Activation body:

```json
{
  "lockDigest": "sha256:…",
  "expectedBindingVersion": 2,
  "operation": "deploy",
  "targets": [
    { "kind": "aws-batch", "jobDefinitionArn": "arn:aws:batch:us-west-2:123456789012:job-definition/buffer-features:7" }
  ]
}
```

`operation` is `deploy` or `rollback`. A rollback must name exactly the binding's
`previousLockDigest`; anything else is refused. Every refusal is data, never an exception:

| Reason | Meaning |
|---|---|
| `LOCK_NOT_FOUND` | Publish the lock first. |
| `PROCESS_MISMATCH` | The lock belongs to another process. |
| `STALE_BINDING_VERSION` | The live version moved since the deployment read it; re-read and retry from the current state. |
| `INCOMPATIBLE_SERVER_CONTRACT` | The server's execution contract version is outside the lock's window. |
| `TARGET_UNSUPPORTED` | No activatable execution workload of that kind here, or a `kubernetes-job` target on a custom-code process (ADR-0063: custom code is AWS-Batch-only). |
| `TARGET_IDENTITY_MISSING` | An `aws-batch` target needs the lock's job-definition name with an explicit positive revision in its ARN. |
| `TARGET_NOT_READY` | The exact image digest and provider revision are not registered on a compatible custom-code workload. Deploy and register that worker path first. |
| `ROLLBACK_TARGET_MISSING` / `ROLLBACK_TARGET_MISMATCH` | No previous release, or the lock is not the previous release. |

## What a job executes

A submission that names a process (`customcode.process: <name>`) is admitted against the live
binding once. The server stamps the release into the job's durable spec: `customcode.repo_url`,
`customcode.git_ref` (the lock's source commit), `customcode.entrypoint`, `customcode.runtime`
from the lock; `batch.job_definition_arn` for the AWS Batch target; and
`honua.release.process`, `honua.release.version`,
`honua.release.lock_digest` and `honua.release.binding_version`. A caller may repeat the same
values but a different `git_ref`, repository, entrypoint or runtime is refused: a submission that
names a release cannot also point at other code.

For Python, `customcode.deps_manifest` is the dependency-lock path and must be a pip-compatible
requirements file named `*.txt` or `requirements.lock`. Admission rejects other names before
creating a job or minting a token: export uv/Poetry/Pipenv locks to pip requirements before
building the release. The name is a format declaration; the worker still validates the contents
during restoration. For .NET, the
matching registered workload must declare `customcode.deps_manifest` as the repository-relative
`.csproj` path. Admission refuses a missing project or a conflicting caller value before minting
a token. The v1 release lock records `packages.lock.json`, not a build-project path; the project
setting is resolved from the registered workload at admission and persisted in that job. A change
to that workload setting can affect new admissions, even without changing the release binding.
The dependency-lock digest is recorded as provenance; this API does not independently verify
the worker's dependency restoration against that digest.

The spec is persisted at admission. Replaying an admitted job or requeueing its persisted
record keeps that release. Changing the binding affects subsequent admissions only.
Workflow-run pinning and schedule reconciliation require separate orchestration integration;
this API alone does not establish those guarantees. Built-in processes that ship inside the server image are not
bound this way; their rollback granularity is the server image.

## Deployment smoke and output transport

The deployment workflow can submit the active binding through
`POST /api/v1/admin/gp/process-releases/bindings/{process}/jobs`. Its JSON body has a
`params` object and optional `declaredScope` entries containing `serviceId`, optional
`layerId`, and `access` (`read` or `write`). An optional `Idempotency-Key` header preserves
the original job on identical replay, including after a binding changes. Changed inputs
under the same key return 409. The response is 202 with `jobId`, `statusUrl`,
`diagnosticsUrl`, and `artifactsUrl`; `Location` identifies the status URL. Submission
requires admin authentication and the shared custom-code execution permission. Source,
image, credentials, and output destinations are resolved by the server.

Custom workers upload through the server artifact broker using their job-bound callback
credential, without cloud storage credentials. The broker accepts only the current
Running attempt and its current token. An early worker retries while the provider's
Running observation is being persisted. Each file name is immutable within that attempt.
The broker reserves capacity atomically before accepting bytes: at most 32 files,
64 MiB per file, and 256 MiB total per job artifact list. The reserved harness journal
counts toward those limits and has a 1 MiB ceiling. One slot and 1 MiB are reserved
inside the totals until the journal is uploaded, limiting user output to 31 files and
255 MiB before journal publication. Failed or interrupted uploads retain
their capacity reservation. A matching reservation allows at most three upload attempts,
including the first. Remote retries retain all broker reservations and outputs in
the cumulative job budget. A retry is refused with a durable diagnostic if its
mandatory journal no longer fits; the existing finite attempt cap still applies. Recovery and completed output retries accept only identical bytes;
completed journal retries re-run ingestion of the original immutable journal. Cloud
objects expire after seven days.

The server requires configured AWS S3 or Azure Blob storage that supports conditional
create. Admin artifact listings expose authenticated same-origin download links, labels,
and byte sizes. Download requires the canonical job-read and ownership checks; the
callback token itself grants no job-read permission. Files are served as attachments
after validating their complete bytes against the broker-computed SHA-256 receipt,
with that digest as the ETag. A deployment smoke can compare downloaded bytes to
its committed expected-output fixture and verify the release identity in diagnostics.
Worker journal ingestion requires its registered ingestion service; uploads fail closed
when durable storage or journal ingestion is unavailable.

## Schedules

Activation does not create, modify, or duplicate schedules. The binding response identifies
schedule reconciliation as pending until the orchestration integration is qualified.

## Storage

Releases, bindings and the append-only activation history live in `honua.gp_process_release`,
`honua.gp_process_binding` and `honua.gp_process_binding_history` (migration
`131_CreateGpProcessReleases.sql`). Activation locks the process's binding row inside one
transaction. The server requires the migrated database. The devkit and isolated tests can use an
in-process store. Non-PostgreSQL providers also retain that process-local fallback: their
release state does not survive a restart and is not shared across replicas. Durable production
release deployment in this slice requires PostgreSQL. `Database:Schema` selects the release
tables alongside the other migrated metadata tables.

The server records the authenticated principal as `activatedBy`; activation requests do not
accept a caller-supplied actor identity.

## Scoped SDK callbacks

The custom worker's Python `ctx.client` and .NET `context.Client` supply the
job's bearer capability and `X-Honua-Job-Id` context automatically. The server
accepts this credential only while its persisted custom-code job is running,
with no cancellation requested, and only for the current execution attempt and
its exact persisted token. A replaced, revoked, expired, terminal-job or
mismatched-job capability cannot authenticate a callback.

Data access uses the frozen intersection of the submitter's grants and the
job's declared resource scope. A read-only scope cannot edit features; a grant
for one service or layer cannot reach another. Later owner role changes and
OIDC default-role configuration do not widen this capability. Tenant identity
comes from the pinned owner snapshot, and a conflicting tenant header is
rejected. Job credentials do not grant administrative access. An explicit
invalid job credential is refused even when the request also supplies an
administrative API key.
