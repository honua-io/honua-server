# ADR-0063: Custom-code (custom GP tool) execution is AWS-Batch-only

## Status

Accepted (2026-07)

## Context

Honua lets an operator register a **custom geoprocessing tool** — user-authored
code (a Python entrypoint or a .NET `IGeoprocessingTool`) fetched from a pinned
git commit and run as a geoprocessing job. This is the `custom-code` runtime
profile in the geoprocessing job model
(`Honua.Geoprocessing/.../CustomCode`). Unlike a built-in process
([ADR-0057](0057-geoprocessing-capability-boundaries.md)), the code that runs is
**operator-supplied and untrusted** from the server's perspective: it can read
files, open sockets, spawn processes, and attempt to reach ambient cloud
credentials.

The execution substrate for a geoprocessing job is chosen by the control-plane
**execution-workload catalog** (`ControlPlane:ExecutionWorkloads`). A custom-code
job is routed (in `GeoprocessingJobDispatcher.ResolveWorkloadAsync`) to the
workload whose `RuntimeProfile == "custom-code"`; that workload names an
`IBatchComputeBackend` by `(Backend, TargetKind)`. Several backend families are
wired on trunk:

| Backend name          | `TargetKind`    | Isolation                                   |
|-----------------------|-----------------|---------------------------------------------|
| `honua-aws-batch`     | `AwsBatch`      | isolated, cloud-managed per-job container    |
| `honua-azure-batch`   | `AzureBatch`    | cloud batch                                  |
| `honua-kubernetes-job`| `KubernetesJob` | cluster job                                  |
| `local`               | `KubernetesJob` | **on-host**, in-process local queue          |
| `honua-local-process` | `LocalProcess`  | **on-host**, server-managed subprocess pool  |

The on-host backends (`local`, `honua-local-process`, ADR-0060) are legitimate,
zero-cloud executors for *trusted* built-in GP/ETL work on on-prem/air-gapped
hosts. But nothing at configuration time stopped an operator from pointing the
`custom-code` workload at one of them — which would run **untrusted operator code
as a subprocess of (or in-process with) the honua-server host**.

A local-process custom-code backend was evaluated in **honua-server#2672
(`LocalProcessCustomCodeBackend`) and CLOSED as a no-go**. Its isolation depended
on deployment-specific boundaries plus an operator-configured privilege drop, and
its unconfined mode let untrusted code read honua-server's own secrets and
environment and fork-bomb the host process. The existing runtime backstop
(`CustomCodeDispatchJobExecutor` fails a custom-code job that is ever claimed
in-process) is a last-resort fence, not a configuration-time gate: it fires only
*after* a misconfiguration has shipped.

## Decision

**Custom-code (custom geoprocessing tool) execution is AWS-Batch-only.** Untrusted,
operator-supplied geoprocessing code runs **only** inside an isolated,
cloud-managed **AWS Batch** container — never as a subprocess of, or in-process
with, the honua-server host, and (for now) not on any other batch family either.

Concretely:

1. **The only sanctioned substrate for the `custom-code` runtime profile is the
   AWS Batch backend family** (`TargetKind = AwsBatch`, `Backend =
   honua-aws-batch`), executing the sanctioned worker images
   (`docker/worker-customcode-python`, `docker/worker-customcode-dotnet`).
2. **On-host execution of custom code is prohibited.** The `local` and
   `honua-local-process` backends remain available for *trusted* built-in GP/ETL
   workloads; they must never carry the `custom-code` profile.
3. **Other cloud batch families (Azure Batch, Kubernetes Job) are not sanctioned
   for custom code** at this time. They are isolated from the host but have not
   been through the same sandbox review as the AWS Batch worker images (non-root
   user, credential/token scrub, network posture). Extending custom-code to
   another family is a deliberate future decision that must add the equivalent
   sandbox guards and amend this ADR — not a config change.

This does not narrow where *built-in* GP runs (ADR-0057 / ADR-0060 are
unchanged); it constrains only the untrusted `custom-code` profile.

## How this is enforced

1. **Startup configuration gate (primary).**
   `ControlPlaneOptionsValidator` (registered with `ValidateOnStart`) fails
   startup when any `ControlPlane:ExecutionWorkloads` entry declares
   `RuntimeProfile = "custom-code"` with a `TargetKind` other than `AwsBatch`. The
   failure message names the offending workload and points here. A future PR that
   reintroduces an on-host (or other) custom-code backend therefore trips an
   explicit, documented gate rather than silently landing.
   *(Unit tests: `ControlPlaneOptionsValidatorTests` — a non-AWS-Batch custom-code
   workload fails; an AWS-Batch one, and an on-host **non**-custom-code workload,
   succeed.)*
2. **Runtime claim fence (backstop).** `CustomCodeDispatchJobExecutor` fails a
   custom-code job that is ever claimed by an in-process worker, so even if a job
   reached a host worker it would fail loudly instead of running untrusted code in
   the server process.
3. **Sanctioned-container defense-in-depth.** The AWS Batch custom-code worker
   images run as a **non-root** user (`USER 1001:1001`) and the harness **scrubs
   `HONUA_JOB_TOKEN`** and the AWS ambient-credential env vars
   (`AWS_CONTAINER_CREDENTIALS_*`, `ECS_CONTAINER_METADATA_URI*`, static keys)
   **before importing user code**, leaving the tool only a least-privilege,
   job-bound scoped client. These guards are asserted by tests
   (`docker/worker-customcode-python/harness/tests`: `test_sandbox.py` for the
   token scrub, `test_dockerfile_guards.py` for the non-root `USER` in both worker
   Dockerfiles) so they cannot silently regress. The isolation expectation is that
   the Batch job's network egress and task-role scope are restricted by the
   deployment IaC; user code is denied the task role by the scrub regardless.
4. **SDK exposes no local-execution path.** `honua-sdk-python` has **no**
   custom-code job-submission surface and cannot influence backend selection
   (backend is server configuration). Its ArcPy/ModelBuilder migration codemod
   translates recognized tools only to **built-in server processes**
   (`EXECUTABLE_PROCESS_IDS`); unrecognized tools become `manual-review`, never an
   auto-submitted custom-code job. A tripwire test locks in the absence of any
   custom-code/local-execution surface in the SDK.

## Consequences

- Untrusted operator code has exactly one execution path, and that path is an
  isolated cloud-managed container — its blast radius excludes the honua-server
  process, its secrets, and its host.
- Operators who want custom GP tools must run an AWS Batch substrate; a
  deployment without one cannot enable custom code (submission fails cleanly
  rather than falling back on-host). This is an accepted trade for the security
  boundary.
- On-prem/air-gapped deployments that lack AWS Batch cannot run custom code
  today. Supporting custom code on an isolated non-AWS substrate (e.g. a hardened
  Kubernetes Job) is deferred and, per Decision 3, requires porting the container
  guards and amending this ADR.
- The guard is additive and fail-closed: it locks in behavior that is already the
  intent, so no currently-valid deployment regresses.

## References

- [ADR-0057: Geoprocessing capability boundaries](0057-geoprocessing-capability-boundaries.md)
- [ADR-0060: Two-Plane Operability Architecture — substrate-neutral executors](0060-two-plane-operability-architecture.md)
- honua-io/honua-server#2672 (`LocalProcessCustomCodeBackend`) — evaluated and **closed** as a no-go (local-process custom-code execution)
- Sanctioned worker images: `docker/worker-customcode-python`, `docker/worker-customcode-dotnet`
