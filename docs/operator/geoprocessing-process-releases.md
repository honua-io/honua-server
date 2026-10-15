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
  "actor": "gp-deploy-process run 123",
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
| `TARGET_IDENTITY_MISSING` | An `aws-batch` target needs the job definition ARN with its revision. |
| `ROLLBACK_TARGET_MISSING` / `ROLLBACK_TARGET_MISMATCH` | No previous release, or the lock is not the previous release. |

## What a job executes

A submission that names a process (`customcode.process: <name>`) is admitted against the live
binding once. The server stamps the release into the job's durable spec: `customcode.repo_url`,
`customcode.git_ref` (the lock's source commit), `customcode.entrypoint`, `customcode.runtime`
and `customcode.deps_manifest` from the lock; `batch.job_definition_arn` for an AWS Batch target
and `k8s.image` for a Kubernetes Jobs target; and `honua.release.process`, `honua.release.version`,
`honua.release.lock_digest` and `honua.release.binding_version`. A caller may repeat the same
values but a different `git_ref`, repository, entrypoint or runtime is refused: a submission that
names a release cannot also point at other code.

The spec is persisted at admission. Replaying an admitted job or requeueing its persisted
record keeps that release. Changing the binding affects subsequent admissions only.
Workflow-run pinning and schedule reconciliation require separate orchestration integration;
this API alone does not establish those guarantees. Built-in processes that ship inside the server image are not
bound this way; their rollback granularity is the server image.

## Schedules

Activation does not create, modify, or duplicate schedules. The binding response identifies
schedule reconciliation as pending until the orchestration integration is qualified.

## Storage

Releases, bindings and the append-only activation history live in `honua.gp_process_release`,
`honua.gp_process_binding` and `honua.gp_process_binding_history` (migration
`131_CreateGpProcessReleases.sql`). Activation locks the process's binding row inside one
transaction. The server requires the migrated database. The devkit and isolated tests can use an
in-process store; production registration always uses the durable store.
