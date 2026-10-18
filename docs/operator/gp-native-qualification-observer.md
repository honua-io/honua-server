# Native GP qualification observations on AWS

The native lifecycle obligation remains server #3809 and release #123/#381,
coordinated under release #652. `gp_native_observer.py` supplies OS and physical
filesystem observations. It does not cancel jobs, grant release admission, repair
durable state, or turn the existing local Compose receipts into AWS evidence.

The release producer must invoke the installed, locked official MCP client for
`honua_cancel_job`, repeat cancellation, and independently read the canonical job
and late result. An acknowledgement is not cessation. Redis-off remains a typed
nonexecution refusal; EKS remains Preview. The existing GP denominator is unchanged.

## Governed transport

Use **ECS Exec into an explicitly enabled diagnostic sidecar**, not an HTTP
debug endpoint. Pin the sidecar and native worker to the same reviewed native
image digest containing `/app/gp_native_observer.py`. Override only the sidecar
entry point to `python3` with `-c`, `import time; time.sleep(86400)`; it does not
start a second worker. Declare `pidMode: task` so the separate observer process
can read the original worker and child process. Mount the worker scratch,
qualification-barrier and shared-output volumes at the same absolute paths in
both containers, **read-only in the sidecar**. Keep the worker's existing user,
readonly-root, capability and credential restrictions.

ECS Exec needs a writable filesystem for its agent. The auxiliary diagnostic
sidecar may supply that; do not disable the application's readonly-root setting
to make a probe work. This is opt-in qualification deployment configuration, not
a default production sidecar. Both AWS ECS and AWS Batch support ECS Exec;
review the [ECS requirements](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html)
and [Batch Fargate configuration](https://docs.aws.amazon.com/batch/latest/userguide/create-job-definition-Fargate.html)
before adopting a task definition. A missing agent or unsupported task profile
is a blocked observation.

For ECS, the original worker task must remain running after the exact native
child disappears. For Lambda+Batch, bind the provider job and attempt to its ECS
task **before** cancellation. Independently require that same task to reach
STOPPED, with its container exit and attempt stop observations retained. Missing
provider records are not evidence of cessation. After the Batch task stops, use
a separately identified, still-running store observer task with the **same EFS
filesystem, access point, root directory and attestation**, mounted read-only.
Never substitute a fresh empty volume, the API Lambda filesystem, or the
runner's filesystem. The native scratch filesystem disappears with the Batch
task; persistent effects must be inventoried through the independent EFS mount.

The release-side preflight must inspect actual task definitions, running task
and container IDs, image digests, PID mode, mount source/read-only flags, volume
identity and ExecuteCommandAgent state. Merely providing expected task ARNs or
an observation JSON file is insufficient. No task, mount, permission or resource
is created or changed by the observer.

## Explicit policy

Mount a JSON policy read-only at
`/var/run/honua/qualification/observer-policy.json`:

```json
{
  "schema": "honua.gp-observer-policy/v1",
  "enabled": true,
  "role": "worker",
  "barrierRoot": "/var/run/honua/qualification",
  "scratchRoot": "/tmp/honua-gdal-worker",
  "outputRoot": "/var/lib/honua/gp-outputs",
  "keyPrefix": "gp/outputs",
  "storeReference": "operator-declared-store",
  "configurationDigest": "<64 lowercase hex characters from the existing store attestation>"
}
```

The independent persistent-store observer uses `role: store-observer` and null
`barrierRoot`/`scratchRoot`; its output root, prefix and store identities must
match. Both roles require the existing `.honua-gp-store.json` marker with local
provider and shared-persistent storage. Missing roots, policy, attestation or
matching job barrier fail closed. The worker must explicitly set the existing
`HONUA_GP_QUALIFICATION_BARRIER_ROOT`; its native-process-started record supplies
the actual worker and child IDs. No new simulated operation is introduced.

The maintained producer sends a fresh 128-bit nonce and invokes:

```text
python3 /app/gp_native_observer.py --job JOB --nonce NONCE --phase before
python3 /app/gp_native_observer.py --job JOB --nonce NONCE --phase after --expected BASE64URL_BEFORE_PROCESS
python3 /app/gp_native_observer.py --job JOB --nonce NONCE --phase store
```

`before` requires a live, non-zombie direct child and records kernel boot ID,
PID-namespace inode and worker/child start ticks. `after` requires the original
worker identity and barrier bytes, and refuses any remaining child PID,
including reuse. Filesystem inventories hash every regular job-scoped file;
symlinks, special files, races, traversal, more than 4,096 files or more than
128 MiB fail closed. No file contents, process arguments, environments or
credentials are emitted. A reader must still bind the task/container/image,
nonce, observation time, job and source/run provenance independently.

## Permissions and proof boundaries

The qualification operator needs `ecs:DescribeTasks`,
`ecs:DescribeTaskDefinition`, and `ecs:ExecuteCommand` restricted to the exact
owned cluster/task and diagnostic container; Batch additionally needs its exact
job, queue and definition read APIs. Task roles need only the required
`ssmmessages` control/data channels for Exec. Restrict direct SSM session access,
retain normal Exec auditing, and satisfy the documented network/agent/KMS
requirements. Do not grant an arbitrary caller production shell access.

Native scratch/output inventories prove the filesystem effect scope of the
selected native workload. A workload that can write a database or external sink
also needs independently bound pre/post readback of that exact target; this
observer cannot certify those effects. If cancellation follows a committed
write, disclose the commit rather than claiming rollback. Tests with local
processes/files prove only these observer contracts. Exact-image AWS execution,
installed-client binding, late-result refusal and the full fault matrix remain
separate required evidence.
