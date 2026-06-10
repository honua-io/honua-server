# ADR-0052: Kubernetes closed-loop promotion via Argo Rollouts

## Status

Accepted (`honua-io/honua-server#1554`, child of `#1537`).

## Context

The operator-promotion control plane (`src/Honua.Server/Features/ControlPlane/`)
drives Console-initiated release packages through a durable, leased reconciler
(`DeployWorkflowReconciler`) over the `IDeployBackend` abstraction and the
`WorkflowOperationRecord` lifecycle. Every managed deploy target is already
closed-loop — AWS Lambda alias, AWS ECS + ALB, Azure Container Apps revision,
and Azure Functions slot all observe real provider state and drive automatic
promotion or rollback, gated by `PrometheusDeployTelemetrySignalEvaluator`.

Kubernetes was the one open target. `KubernetesGitOpsDeployBackend`
(`GitOpsDeployBackends.cs`) is a hand-off stub: `ObserveAsync`/`RollbackAsync`
return `ManualInterventionRequired` / "confirm out of band". The Console could
stage and record a K8s promotion, but the weighted canary and auto-rollback were
delegated entirely to whatever external controller the operator ran, with no
status reflected back into the operation. If Kubernetes + Helm is a primary
enterprise deploy substrate, this left the promotion UX non-uniform: some targets
fully automated, K8s silently manual.

`#1537` set the preferred direction: *a first-class integration with an external
progressive-delivery controller whose status is reflected back into the operation
lifecycle*, rather than a from-scratch in-process Kubernetes rollout engine. The
decision in front of us is which controller, and how its status maps onto the
`WorkflowOperationStatus` lifecycle the reconciler already understands.

Constraints that dominate:

1. **Do not rebuild a rollout engine.** Teams that run Kubernetes at the scale
   where weighted canary matters already standardize on a progressive-delivery
   controller. Reimplementing traffic-splitting, ReplicaSet management, analysis
   runs, and metric-driven aborts in `honua-server` would duplicate a mature
   ecosystem and own a large surface we do not need to own.
2. **Stay behind `IDeployBackend` + `WorkflowOperationRecord`.** The Console
   promotion flow, the leased reconciler, the telemetry gate, and the precomputed
   rollback plans must be unchanged. The new backend is just another
   `IDeployBackend` selected by `Backend` name under
   `DeployTargetKind.Kubernetes`.
3. **No new heavy dependency.** The existing `KubernetesJobBatchComputeBackend`
   reaches the Kubernetes API through a narrow raw-HTTP client
   (`KubernetesJobClient`) instead of the official `KubernetesClient` NuGet
   package, to keep the trim/AOT surface small and reuse the in-cluster
   service-account / out-of-cluster CA-trust auth chain. The rollout integration
   should reuse that same path.
4. **Model stays Console-driven (per `#1537` non-goals).** Honua does not become
   an autonomous Flux/Argo pull-from-git reconciler. The GitOps manifest remains
   the declarative artifact; Honua initiates and observes the rollout. The unused
   `GitOpsWatchManifestPath` scaffolding stays out of scope.

The two mainstream candidates are **Argo Rollouts** and **Flagger**:

- **Argo Rollouts** introduces a first-class `Rollout` custom resource that
  replaces the `Deployment`. Promotion and abort are explicit, imperative verbs
  against the resource: set the pod-template image to start, clear the pause to
  promote, set `status.abort=true` to roll back. The rollout's current canary
  weight, phase, pause conditions, and stable/current ReplicaSet hashes are all
  on `.status`, so an external orchestrator can both *drive* and *observe* the
  rollout through a small, well-defined set of REST operations.
- **Flagger** drives a canary by mutating a target `Deployment` and watching its
  own `Canary` resource; it is more metrics-loop-centric and expects to own the
  analysis cadence. Externally initiating a specific promotion or abort step,
  and reading back a discrete "paused at weight N, ready for the gate" signal, is
  a weaker fit for Honua's reconciler, which already runs the telemetry gate and
  wants to issue an explicit promote/abort.

## Decision

Integrate with **Argo Rollouts** as a new closed-loop Kubernetes deploy backend,
`honua-kubernetes-argo-rollouts` (`KubernetesArgoRolloutsDeployBackend`), and
reflect the `Rollout` resource's controller-reported status back into the
`WorkflowOperationRecord` lifecycle. The progressive stepped ramp itself
(`5→25→50→100`, pauses, analysis) is owned by the cluster-side
`spec.strategy.canary.steps` on the `Rollout`; Honua initiates the rollout,
observes the step weight and phase, and gates promotion/rollback through the
existing shared telemetry-gated reconciler.

The backend coexists with the existing `honua-gitops-kubernetes` passthrough
stub under `DeployTargetKind.Kubernetes`; targets choose by `Backend` name, so
operators who genuinely want pure out-of-band GitOps hand-off keep that option.

### Controller access

A new narrow REST adapter, `ArgoRolloutsClient` (`IArgoRolloutsClient`), speaks
the Argo Rollouts `argoproj.io/v1alpha1` `Rollout` API:

- `GetRolloutAsync` — `GET .../rollouts/{name}`, parse `.status`.
- `SetImageAsync` — strategic-merge `PATCH` of
  `spec.template.spec.containers[name].image` (start the rollout; the container
  name is the merge key so sibling containers and pod-template fields survive).
- `PromoteAsync` — merge-`PATCH` the `status` subresource to null
  `pauseConditions` and clear `abort`, then merge-`PATCH` `spec.paused=false`
  (mirrors `kubectl-argo-rollouts promote`).
- `AbortAsync` — merge-`PATCH` `status.abort=true` (mirrors
  `kubectl-argo-rollouts abort`), which reverts traffic to the stable revision.

To avoid duplicating the credential chain, the in-cluster service-account /
out-of-cluster CA-trust auth and request shaping were extracted from
`KubernetesJobClient` into a shared `KubernetesApiRequestFactory`, consumed by
both the Job batch client and the new rollout client over the same
`control-plane-kubernetes` HTTP client. No new NuGet dependency is added.

### Status mapping

The backend maps Argo `Rollout` status onto `WorkflowOperationStatus` so the
reconciler's existing observe → telemetry-gate → promote/rollback loop drives
real transitions:

| Argo Rollout observation | Workflow status | Signal |
|---|---|---|
| `Progressing`, not paused | `Reconciling` | — |
| `Paused` at the configured canary weight (`status.canary.weights.canary.weight`) | `Reconciling` | `PromotionRecommended=true` |
| `Healthy` and `currentPodHash == stableRS` | `Succeeded` | terminal |
| `Healthy` but `currentPodHash != stableRS` (mid-ramp) | `Reconciling` | — |
| `Degraded`, or `status.abort=true` | `Reconciling` | `RollbackRecommended=true` |
| During `RollbackRequested`: reverted to stable (`Healthy`, hashes equal) | `RolledBack` | terminal |
| Rollout resource not found | `Failed` | configuration error |

`PromoteAsync` issues the promote verb and stays `Reconciling` until a later
`ObserveAsync` sees `Healthy` + stable; `RollbackAsync` issues the abort verb and
returns `RollbackRequested`, which subsequent observations resolve to `RolledBack`
once the controller has reverted. When the controller does not report both pod
hashes (older Argo versions), `Healthy` is treated as converged so the operation
still terminates. Canary-weight gating requires `telemetry.connection` at plan
time, matching the AWS ECS + ALB contract, so the auto-rollback signal always has
a metrics source.

### Required target parameters

`kubernetes.namespace` (or in-cluster auto-detected), `kubernetes.argo.rollout_name`
(or `targetName`), `kubernetes.argo.container_name`, and `desiredRevision` (the
image to roll out). Optional `deployment.canary_weight_percentage` /
`kubernetes.argo.canary_weight_percentage` with a required `telemetry.connection`.

## Consequences

**Easier.** Kubernetes promotions become closed-loop and uniform with the other
managed targets: weighted canary + automatic rollback driven by the same Console
flow, the same leased reconciler, and the same telemetry gate, with rollout status
visible on the operation record instead of `ManualInterventionRequired`. The auth
chain is now shared between the Job and rollout clients, reducing duplication.
Provider error text (resource names, namespaces, request detail) is sanitized off
the durable operation record and only the structured log carries the raw
Kubernetes error.

**More constrained.** This backend requires the operator to run Argo Rollouts on
the cluster and to model their workload as a `Rollout` resource (not a plain
`Deployment`); a `honua-helm` change to optionally ship the `Rollout` CR is the
expected follow-on when teams adopt it. The stepped canary ramp and analysis
templates are authored cluster-side, not in Honua — Honua observes and gates, it
does not define the steps. Teams who standardize on Flagger or a pure GitOps
hand-off are not served by this specific backend; they keep the
`honua-gitops-kubernetes` passthrough. Live verification against a real cluster +
Argo controller is out of scope for unit CI; the backend is covered by mocked
controller-status tests mirroring `AwsEcsAlbDeployBackendTests`, and a live test
lane can follow the `AwsEcsAlbDeployBackendLiveTests` pattern when a cluster is
available.
