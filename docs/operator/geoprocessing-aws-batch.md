# Routing geoprocessing jobs to AWS Batch (#2165)

By default Honua runs geoprocessing (GP) jobs on the **local / Kubernetes Job**
baseline workload (`geoprocessing-local`). To run GP jobs on **AWS Batch**
instead — using the Fargate job-definition pool provisioned by the
[honua-iac](https://github.com/honua-io) serverless substrate — you supply the
substrate ARNs to the `geoprocessing-aws-batch` execution workload that already
ships (gated off) in `appsettings.json`.

This is config-only: no code or redeploy of the GP execution logic is needed.

## How routing works

1. A GP job is submitted (GPServer REST, OGC API Processes, or gRPC).
2. `GeoprocessingJobService` resolves the GP execution workload from
   `ControlPlane:ExecutionWorkloads`. If a **fully-configured** AWS Batch
   workload is present it is preferred over the local baseline; otherwise the
   job falls back to `geoprocessing-local`.
3. The workload's `Parameters` are merged onto the job's
   `ExecutionJobSpec.Parameters`, and the job is handed to the
   `honua-aws-batch` backend (`AwsBatchComputeBackend`).
4. The backend selects the job-definition **tier** from the job's ephemeral
   storage need (`batch.ephemeral_gib` → `s`/`m`/`l`/`xl`) and submits to the
   queue.

## The activation gate

The committed `geoprocessing-aws-batch` workload has **empty** ARN parameters,
so it is dropped at startup by the registry's activation gate and GP keeps
running locally. The workload becomes active only once you provide:

- `batch.job_queue_arn` — the GP job-queue ARN, **and**
- at least one job-definition ARN — either the single `batch.job_definition_arn`
  or one or more of the per-tier `batch.job_definition_arn.{s,m,l,xl}` keys.

If those are absent, GP routing cleanly falls back to local with no change to
existing deploys or tests.

## Substrate outputs → config

The honua-iac serverless module exports these outputs:

| honua-iac output | Honua workload parameter |
|---|---|
| `gp_job_queue_arn` | `batch.job_queue_arn` |
| `gp_job_definition_arns["s"]` | `batch.job_definition_arn.s` |
| `gp_job_definition_arns["m"]` | `batch.job_definition_arn.m` |
| `gp_job_definition_arns["l"]` | `batch.job_definition_arn.l` |
| `gp_job_definition_arns["xl"]` | `batch.job_definition_arn.xl` |
| deployment region | `batch.region` |

The four tiers differ only by ephemeral (scratch) storage:
`s` = 20 GiB, `m` = 50 GiB, `l` = 100 GiB, `xl` = 200 GiB. vCPU / memory /
timeout / retry stay per-submit overrides and are unaffected by tier selection.

> **Tier selection.** The runtime tier selector that maps `batch.ephemeral_gib`
> to the right `batch.job_definition_arn.{tier}` is on `trunk` (#2181). When no
> per-tier keys are configured the backend honors the **single**
> `batch.job_definition_arn` key for back-compat. The parameter-key contract
> (`batch.job_queue_arn`, `batch.region`, `batch.job_definition_arn`,
> `batch.job_definition_arn.{s,m,l,xl}`) is identical on both sides.

## Per-job resource sizing (#2165)

Per-job sizing is **runtime and instant** — no terraform, no agent in the job
path. Each GP job carries a resource profile that is mapped onto the
`batch.*` submit parameters the backend reads:

| Profile dimension | Spec param the backend reads | Effect |
|---|---|---|
| vCPU | `batch.vcpus` | `SubmitJob` resource override |
| Memory (MiB) | `batch.memory_mib` | `SubmitJob` resource override |
| GPU count | `batch.gpu_count` | `SubmitJob` resource override |
| Attempt timeout (s) | `batch.timeout_seconds` | `SubmitJob` timeout override |
| Retry attempts | `batch.retry_attempts` | `SubmitJob` retry override |
| Ephemeral (GiB) | `batch.ephemeral_gib` | selects the job-definition **tier** |
| CPU architecture | `batch.arch` | reserved for the iac arch/image tier set |

The effective profile is the **heaviest catalog-derived default** across the
plan's steps (raster/surface → largest tier, native GDAL → mid tier, ordinary
managed → smallest tier), then overridden field-by-field by any explicit
per-job request values supplied under the `gp.resource.*` keys (for example
`gp.resource.vcpus`, `gp.resource.memory_mib`, `gp.resource.ephemeral_gib`,
`gp.resource.arch`). Precedence is: explicit request &gt; per-job derived
profile &gt; workload baseline. The local/Kubernetes baseline does **not**
carry these keys (they are meaningless off AWS Batch), preserving the GP Devkit
local-runner spec parity.

## Health-gating a GP substrate deploy

When the durable GP substrate (compute-env / queue / IAM / ECR / job-def tier
set) is deployed via GitOps, gate it with the **`gp-batch`** deploy telemetry
policy preset. GP per-job metrics are burstier than steady HTTP traffic, so the
preset bakes longer (5 min) and the anti-flap gate defaults to requiring
several consecutive breaching scrapes (rather than one) before an automatic
rollback fires — set `telemetry.policy=gp-batch` on the deploy operation. An
explicit `telemetry.rollback.consecutive_breaches` still overrides the default.

### appsettings override

```json
{
  "ControlPlane": {
    "ExecutionWorkloads": [
      {
        "WorkloadId": "geoprocessing-aws-batch",
        "Kind": "Geoprocessing",
        "TargetKind": "AwsBatch",
        "Backend": "honua-aws-batch",
        "WorkloadName": "Geoprocessing (AWS Batch)",
        "Parameters": {
          "batch.job_queue_arn": "arn:aws:batch:us-east-1:123456789012:job-queue/honua-gp",
          "batch.region": "us-east-1",
          "batch.job_definition_arn.s": "arn:aws:batch:us-east-1:123456789012:job-definition/honua-gp-s:1",
          "batch.job_definition_arn.m": "arn:aws:batch:us-east-1:123456789012:job-definition/honua-gp-m:1",
          "batch.job_definition_arn.l": "arn:aws:batch:us-east-1:123456789012:job-definition/honua-gp-l:1",
          "batch.job_definition_arn.xl": "arn:aws:batch:us-east-1:123456789012:job-definition/honua-gp-xl:1"
        }
      }
    ]
  }
}
```

> Do **not** commit account-specific ARNs into the shared `appsettings.json`.
> Use an environment-specific override file, a mounted secret/config, or
> environment variables.

### Environment-variable override

The `ExecutionWorkloads` array index of `geoprocessing-aws-batch` depends on
your effective config (in base `appsettings.json` it is index `1`). Dotted
parameter keys are exposed through `ParameterEntries` so they survive the
environment-variable provider:

```bash
ControlPlane__ExecutionWorkloads__1__Parameters__batch.job_queue_arn="arn:aws:batch:us-east-1:123456789012:job-queue/honua-gp"
ControlPlane__ExecutionWorkloads__1__Parameters__batch.region="us-east-1"
ControlPlane__ExecutionWorkloads__1__Parameters__batch.job_definition_arn.s="arn:aws:batch:us-east-1:123456789012:job-definition/honua-gp-s:1"
# ...m / l / xl
```

If the environment provider cannot pass dotted keys, use the
`ParameterEntries` list form instead:

```bash
ControlPlane__ExecutionWorkloads__1__ParameterEntries__0__Key="batch.job_queue_arn"
ControlPlane__ExecutionWorkloads__1__ParameterEntries__0__Value="arn:aws:batch:..."
```

## Verifying it routed

Submit a GP job and confirm the job's spec:

- `Spec.Backend == "honua-aws-batch"`
- `Spec.TargetKind == AwsBatch`
- `Spec.Parameters` contains `batch.job_queue_arn` and the tier ARNs

A misconfigured workload (e.g. queue ARN present but no job-definition ARN)
stays gated off and the job runs locally — check startup logs and the resolved
`Spec.Backend` to confirm which path was taken.
