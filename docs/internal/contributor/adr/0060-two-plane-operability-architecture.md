# ADR-0060: Two-plane operability architecture — substrate-neutral executors over a unified catalog

Status: Proposed
Date: 2026-07-04

## Context

Operating a GIS server — deploy, upgrade, scale, run geoprocessing, tune, troubleshoot, roll back — is the platform's sharpest wedge against ArcGIS Server. ArcGIS makes the *human* the control loop: you monitor, diagnose, fix, scale, and upgrade a Byzantine, stateful, click-configured stack by hand. Honua's opportunity is to close that loop so the operator **supervises** instead of operates.

The control plane already abstracts substrates via two backend interfaces (`src/Honua.Core/Features/ControlPlane/Abstractions/OperationInterfaces.cs`):

- **`IDeployBackend`** (serving plane) — implemented by `AwsEcsAlbDeployBackend`, `AwsEcsGitOpsDeployBackend`, `AwsLambdaGitOpsDeployBackend`, `AzureContainerAppsRevisionDeployBackend`, `AzureFunctionsGitOpsDeployBackend`.
- **`IBatchComputeBackend`** (geoprocessing / execution plane) — implemented by `AwsBatchComputeBackend`, `AzureBatchComputeBackend`, with `IExecutionJobDefinitionRegistry` + `SubmitJobAsync` + `ExecutionJobReconciler`.

GitOps reconcile lives in the server; rollback partly in iac; the AI operator (`honua-devops`) exists but is positioned as private services/implementation-partner tooling. Statelessness is ~70% there (auth/sessions/tokens Redis-backed, jobs reconciled, data in DB/object-store), with local-disk staging leaks (`SceneTilesPublishExecutor`/`SceneTilesetStaging`, `StreamingFileUploadService`, `FileSystemMigrationRunCheckpointStore`). `IDatabaseMigrationRunner` has a `PlanMigrationsAsync` step but no backward-compatibility enforcement. Observability emits OTel/Prometheus but the visualization/UX is half-baked.

## Decision

Adopt a **two-plane, substrate-neutral operability architecture**: Honua owns the operational *semantics*; the substrate is a pluggable executor; both planes project from one git-versioned desired-state catalog.

### Core principle
**The server is a stateless, disposable projection of a git-versioned catalog over externalized data.** Therefore every ops action is the *same* thing: a reviewed **diff to declared state**, applied through **one pipeline** (plan → health-gated apply → auto-rollback), on any substrate. Deploy = upgrade = scale = run-GP = tune = roll back. The operator and the AI copilot learn one model, not five subsystems.

### Two planes, one pattern
- **Serving plane (`IDeployBackend`)** — stateless request/response; rolling replace with health-gating; proxy-fronted.
- **Execution/GP plane (`IBatchComputeBackend`)** — stateless batch jobs (inputs from store → outputs to store); submit / track / retry / cancel / collect; scheduler-fronted.

### Blue/green is NOT the primitive
Because compute is stateless, the primitive is **(statelessness) + (a Honua-owned cutover semantic: rolling replace + health-gate + keep-old-until-healthy) + (a thin executor)** — *not* AWS/K8s-native blue/green. Coupling ops to a cloud's deployment strategy breaks on-prem/air-gapped and is unnecessary given stateless compute.

Executor set (semantics owned by Honua, mechanics by the executor):
- Serving: **YARP-embedded** (default; single-container, on-prem, air-gapped — the proxy ships *inside* Honua), **Envoy-xDS** (K8s / service-mesh / scale), plus existing ECS/ALB, Lambda, Container Apps, Functions.
- GP: **local process-pool** and **K8s Jobs** (neutral), plus existing AWS Batch, Azure Batch.

### GP placement is per job, not per deployment

Merely configuring a cloud executor does not make it the global default for every ordinary GP
job. Before durable job creation, Honua compares the job's runtime profile and declared CPU,
memory, GPU, timeout, retry, architecture, and ephemeral-storage request with each workload's
declared compatibility envelope and current capacity snapshot. Modest work prefers the
low-latency local lane; resource thresholds, object-store affinity, and local capacity pressure
prefer a compatible remote lane. The selected workload/backend, policy version, resource request,
reason code, and fallback flag are persisted before provider submission.

Forced isolation and a raster engine/placement decision are hard requirements: absence or
incompatibility is a refusal, not an implicit change of execution semantics. Ordinary placement
preferences may cross lanes only through the explicit local/remote fallback policy. Custom code
remains outside this ordinary policy and retains the AWS-Batch-only fence from ADR-0063.

### Four workstreams
1. **Substrate-neutral executors** — `YarpRollingDeployBackend` (serving) + local/K8s-Jobs `IBatchComputeBackend` (GP), as peers to the cloud executors. Breaks cloud-lock; delivers the on-prem/air-gapped story.
2. **One catalog spanning both planes** — desired-state including serving config **and** GP compute-env / queue / **worker-image version**; both backends project from it. Upgrade = a single diff bumping serving *and* worker images together.
3. **Expand/contract discipline + gate — in two places**: (a) DB schema (extend `IDatabaseMigrationRunner` to reject/flag non-backward-compatible migrations), and (b) the **serving↔worker job contract** (a vX server must not submit a job a vY worker can't run during a rolling version step).
4. **Observability spine** — rich, GIS-aware, correlated telemetry across both planes (serving p95 / cache / pool; GP queue-depth / duration / cost / spot) feeding: an OOTB curated **Grafana-LGTM** stack (the "Splunk out of the box"), a **GIS-aware console health view** (the differentiated at-a-glance), and the **copilot** (diagnosis → proposed catalog diff). Same data serves all three.

### The AI operator gets a customer seat
The `honua-devops` ops-brain (diagnose / tune / upgrade) proposes catalog diffs through the *same* pipeline; a productized subset surfaces in the console (#193) so the customer's admin **supervises**. The deep delegated/we-run-it operator stays private for the services play.

## Consequences

- **Portability**: identical ops semantics on VM / on-prem / air-gapped / K8s / serverless — the topology-neutral promise made real.
- **Uniformity**: one reviewed-diff-through-one-pipeline model for all ops; the moat is the model + encoded GIS-ops expertise, not any cloud's primitive. Esri (stateful pet, in-place patch) cannot retrofit it.
- **Prerequisites**: close local-disk statelessness leaks (stage-to-scratch, externalize outputs); adopt expand/contract authoring discipline; verify against real cloud (see below).
- **Migration wedge compounds**: "import your ArcGIS services, then never touch a server again."

## Alternatives considered
- **AWS/K8s-native blue/green as the primitive** — rejected: couples ops to a substrate, breaks on-prem/air-gapped, unnecessary given stateless compute.
- **External-only AI operator (current honua-devops posture)** — insufficient for the product wedge; the customer's admin never feels it. Keep for services; productize a seat.
- **Build a Splunk-equivalent** — rejected: ship curated Grafana-LGTM; differentiate in GIS-aware interpretation + remediation, not a log-search engine.

## Verification
Real cloud e2e is the proving ground (lots of moving parts): **#2164** (real-AWS certification tier — Batch/ECS/Lambda + rollback; OIDC, ephemeral, budgeted) and **#2166** (cloud-integration & deploy-safety — emulated + real-cloud, GP + ECS/Lambda/K8s). Extend to the two-plane matrix:
`{serving, GP} × {YARP/local, ECS/Batch, K8s, Azure} × {deploy, rolling-upgrade, expand/contract-migration, rollback, scale, cancel}`.

Tracked by epic **#2457** (Operate-it-for-me — the self-driving GIS platform).
