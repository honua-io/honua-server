# Tile-Cache / PMTiles Batch Worker Contract

This runbook documents how to run long-running tile-cache / PMTiles operations
(`seed`, `warm`, `invalidate`, `purge`, `archive`, `publish`) as durable
`ExecutionJobKind.TileCache` execution jobs on a batch compute backend (AWS Batch
or Kubernetes Jobs) instead of in-process on the serving pod (honua-server#1697).

It pairs with:

- The geoprocessing batch worker contract — the same execution-job substrate
  (`IBatchComputeBackend`, `ExecutionJobStore`, `ExecutionJobReconciler`,
  `JobExecutionService`).
- `ControlPlane:ExecutionWorkloads` / the AWS Batch and Kubernetes Job backends in
  `src/Honua.Server/Startup/BatchAndDeployBackendsRegistration.cs`.

---

## Why

Tile-cache / PMTiles generation historically ran **in-process on the serving
pod**, single-threaded through an in-memory channel and **hard-capped at 5,000
tiles** per job. Large basemaps and customer onboarding stalled deploys and could
not scale beyond one pod. Routing the work onto AWS Batch / Kubernetes Jobs offloads
it to dedicated compute, lifts the tile cap, and lets retry/heartbeat/cancel flow
through the shared execution-job reconciler.

The in-process path remains the **zero-config default**. Batch dispatch is opt-in.

---

## Enabling batch dispatch

Batch dispatch is controlled by the `TileOperations:Batch` configuration section and
requires the durable execution-job substrate (Redis-backed store + queue, the same
substrate geoprocessing uses).

```jsonc
{
  "TileOperations": {
    "Batch": {
      // Off by default — the in-process channel worker handles tile operations.
      "Enabled": true,

      // Batch compute backend adapter id. "local" runs the work in-process via the
      // canonical execution-job machinery (durable record + queue + reconciler) on
      // the same pod; cloud backends offload it.
      //   - "local"            -> in-process worker (LocalBatchComputeBackend)
      //   - "honua-aws-batch"  -> AWS Batch (AwsBatchComputeBackend)
      //   - the Kubernetes Job backend id for K8s
      "Backend": "honua-aws-batch",

      // Backend family used to resolve the adapter.
      //   KubernetesJob | AwsBatch | AzureBatch
      "TargetKind": "AwsBatch",

      // Container image / artifact reference for the worker (ignored by "local").
      "Artifact": "123456789012.dkr.ecr.us-west-2.amazonaws.com/honua-tile-worker:1.0.0",

      // Optional specialized runtime profile (for example a GDAL/tippecanoe image family).
      "RuntimeProfile": "native",

      // Runaway guardrail. The in-process path is fixed at 5,000 tiles to protect
      // the serving pod; batch jobs run on dedicated compute and are bounded by
      // backend timeout/resources, so this defaults to 1,000,000.
      "MaxTiles": 1000000,

      // Backend-specific coordinates merged onto every tile-cache job's
      // ExecutionJobSpec.Parameters. See the AWS Batch / Kubernetes sections below.
      "Parameters": {
        "batch.job_definition_arn": "arn:aws:batch:us-west-2:123456789012:job-definition/honua-tile-worker:1",
        "batch.job_queue_arn": "arn:aws:batch:us-west-2:123456789012:job-queue/honua-tile",
        "batch.region": "us-west-2",
        "batch.vcpus": "4",
        "batch.memory_mib": "8192",
        "batch.timeout_seconds": "10800"
      }
    }
  }
}
```

When `Enabled` is `true` the admin endpoint
`POST /api/v1/admin/tile-operations/jobs` submits an `ExecutionJobKind.TileCache`
execution job to the configured backend; when `false` (or the Redis substrate is
absent) it uses the in-process channel path exactly as before. Job status and
cancellation are served through the **same** admin endpoints in both modes
(`GET /api/v1/admin/tile-operations/jobs/{jobId}`,
`POST .../jobs/{jobId}/cancel`).

---

## Execution-job spec contract

The submission service encodes the request onto `ExecutionJobSpec.Parameters` using
these keys (defined in `TileCacheJobParameterKeys`). The worker-side
`TileCacheJobExecutor` reads them back, so they are the stable wire contract any
worker image must honour:

| Key | Meaning |
|---|---|
| `honua.tilecache.operation` | `seed` / `warm` / `invalidate` / `purge` / `archive` / `publish` (required) |
| `honua.tilecache.service_id` | Target service id (when scoped to a service) |
| `honua.tilecache.layer_id` | Target layer id (when scoped to a layer) |
| `honua.tilecache.tile_matrix_set_id` | Gridset id (for example `WebMercatorQuad`) |
| `honua.tilecache.min_zoom` / `honua.tilecache.max_zoom` | Zoom range |
| `honua.tilecache.bbox` | `minLon,minLat,maxLon,maxLat` |
| `honua.tilecache.max_tiles` | Caller-requested tile ceiling |
| `honua.tilecache.schema_name` | Multi-tenant schema captured at submission |
| `honua.tilecache.compression` | Reserved (managed writer emits uncompressed) |

The target artifact location for `archive` / `publish` is resolved on the worker
from the existing `CloudStorage` / `PMTilesPublish` configuration (`ICloudFileStorage`)
— the produced `.pmtiles` artifact is uploaded to the same bucket/key the in-process
path uses, so no additional parameter is required.

---

## AWS Batch backend

The AWS Batch adapter (`AwsBatchComputeBackend`) reads these keys from
`ExecutionJobSpec.Parameters` (set via `TileOperations:Batch:Parameters` above):

| Key | Required | Meaning |
|---|---|---|
| `batch.job_definition_arn` | yes | Job definition ARN for the tile worker |
| `batch.job_queue_arn` | yes | Job queue ARN |
| `batch.region` | no | AWS region (else SDK default) |
| `batch.vcpus` | no | vCPU resource override |
| `batch.memory_mib` | no | Memory (MiB) resource override |
| `batch.gpu_count` | no | GPU count |
| `batch.timeout_seconds` | no | Per-attempt timeout |
| `batch.retry_attempts` | no | Batch-level retry attempts |
| `batch.share_identifier` | no | Fair-share scheduling identifier |
| `env.*` | no | Extra environment variables passed to the container |

### Job-definition / container image requirements

The container image referenced by the AWS Batch job definition (or the Kubernetes
Job image) must:

1. Run the Honua worker host with the tile-cache executor registered (the same
   `JobExecutionService` worker the geoprocessing jobs use; `ExecutionJobKind.TileCache`
   is registered unconditionally). For a "native" `RuntimeProfile` the image is the
   GDAL/tippecanoe-capable worker image — it must include GDAL and tippecanoe on the
   `PATH` for tile generation.
2. Have network access and credentials for:
   - the Honua database / metadata store (to resolve layers and read features),
   - Redis (durable execution-job store, queue, progress — the worker claims the job,
     reports progress/heartbeat, and writes the terminal state through it),
   - the configured cloud object storage (`ICloudFileStorage`) for `archive` / `publish`
     artifact upload.
3. Honour cooperative cancellation: the worker observes the durable
   `CancellationRequestedAt` signal via heartbeat and stops promptly.

### IAM / RBAC

- **AWS Batch:** the submitting host needs `batch:SubmitJob`, `batch:DescribeJobs`,
  `batch:CancelJob`, `batch:TerminateJob` on the queue/job-definition. The job
  execution role needs read access to the data store secrets, Redis, and
  read/write to the PMTiles bucket.
- **Kubernetes Jobs:** the API host needs a Role/RoleBinding allowing
  `create`/`get`/`delete` on `batch/jobs` in the target namespace; configure it via
  `ControlPlane:Kubernetes`.

---

## Observability & lifecycle

- **Progress:** the worker writes a `TileOperationProgress` to the universal progress
  store (tile counts, phase, archive size, published artifact) AND reports
  percent/phase through the canonical execution-job context, so both the admin
  tile-operations status API and the generic execution-job views stay in sync.
- **Retry / heartbeat:** governed by the job's retry/heartbeat/timeout policies and
  driven by `ExecutionJobReconciler` (expired heartbeat → requeue per retry policy).
- **Cancellation:** `POST .../jobs/{jobId}/cancel` durably records the cancel request
  and signals the backend; the worker stops on its next heartbeat.

---

## Follow-on

Large file imports (`ExtractTransformLoad`) can be moved onto the same
execution-job → batch path. The import executor (`ImportDatasetJobExecutor`)
already exists under `ExecutionJobKind.Geoprocessing`; promoting
`StreamingFileImportService` onto a first-class `ExtractTransformLoad` submission
path that reuses this same backend selection is tracked as a follow-on to #1697
and is intentionally out of scope for the initial tile-cache wiring.
