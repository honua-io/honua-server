# ADR-0064: Geoprocessing Destructive-Plan Approval Lane (Fail-Closed Classifier + Control-Plane Proposal Reuse)

## Status

Accepted (2026-07)

## Context

A geoprocessing (GP) plan submitted through any adapter (GPServer, OGC API
Processes, gRPC `ProcessService`, or the MCP `honua_execute_plan` tool) reaches
the shared `GeoprocessingJobService.SubmitJobAsync` pipeline. Before a job or
progress record is created, the pipeline evaluates whether the plan is
**destructive** (mutates/erases caller-owned data) or a **sink/write** (persists
the input to an external or caller-owned destination). When the operator approval
policy (`Operator:Approval:DestructiveActionsRequireApproval`) requires approval,
submission was **hard-failing** with `GeoprocessingApprovalRequiredException`
(gRPC `FailedPrecondition`, OGC `403`, MCP `failed_precondition`).

Two gaps followed from this (honua-server#2814):

1. **Dead-ended approval.** Unlike the control-plane mutating-operation surface —
   which already routes through the shared operation gateway
   ([ADR-0056](0056-mcp-redesign-unified-governed-surface.md), #1692/#1693/#1696):
   `honua_propose_operation` persists an `AwaitingApproval` proposal, exposes it
   at `honua://proposals/{id}`, and resumes it on human approval — the GP half had
   **no persisted proposal, no status projection, and no resume path**. A gated
   plan simply failed, and the caller had to re-submit from scratch after an
   out-of-band approval.

2. **Fail-open destructive routing.** Destructive routing depended on a
   hand-curated `FrozenSet` of a handful of canonical process ids in
   `ProcessDestructiveClassifier`. Any future mutating process not added to that
   denylist would silently bypass the approval gate.

The canonical destructive-routing decision (route a destructive/sink plan through
the approval gate with `IsDestructive = true`) is already recorded in
[ADR-0029](0029-geoprocess-canonical-model-mappings.md). This ADR records the two
follow-on decisions that make that gate **fail-closed** and **resumable** by
reusing the existing control-plane proposal surface rather than inventing a
GP-local one.

## Decision

### 1. The destructive classifier fails closed (metadata-driven, not a denylist)

`ProcessDestructiveClassifier`'s catalog-aware overloads
(`RequiresApproval(processId, IProcessCatalog)` /
`FindFirstApprovalGatedProcessId(plan, catalog)`) gate a process when it:

- writes to an external/caller-owned sink, **or**
- is **unknown to the process catalog** (a typo or newly-registered process is
  gated by default), **or**
- declares `ProcessExecutionTier.Mutating` and is not on a narrow, explicit
  **SAFE allowlist** (`data-management.copy-features`, which materializes a new
  target without touching the source; `sink.quarantine`, the internal dead-letter
  half of the row-error contract).

The trusted surface is therefore the **safe allowlist**, not a destructive
denylist. A newly-added mutating process is approval-gated until it is
deliberately reviewed onto the allowlist. The `DestructiveActionsRequireApproval`
config bool still governs *whether* the gate is active, but flipping it does not
change *which* processes are considered destructive — the classifier itself
defaults unknown/mutating processes to destructive.

### 2. A gated plan is persisted as a proposal and resumed on approval

When the approval gate fires, `GeoprocessingJobService` no longer only throws. It
**reuses the control-plane proposal/gateway surface**:

- A new `OperationClass.Geoprocess` is added so a GP plan can be carried as an
  `OperationProposal` in the existing `IOperationProposalStore`.
- `IOperationGateway.CreateApprovalProposalAsync` persists an
  `AwaitingApproval` proposal for an operation whose approval requirement was
  **already decided upstream** by the GP destructive gate. It bypasses the edition
  guardrail ladder (the GP gate, not edition, is the decision authority here) but
  reuses the same durable store, `operation.proposed` audit, pending
  notification, and idempotency handling as the ladder-routed path.
- The plan (plus idempotency key, submitter identity, and protocol metadata) is
  serialized as the proposal's opaque execution payload
  (`GeoprocessExecutionPayload`).
- The gated submission still throws `GeoprocessingApprovalRequiredException`, now
  carrying the `ProposalId`, so adapters surface the `honua://proposals/{id}`
  resume path. The MCP error envelope carries `proposalId` + `resourceUri`
  alongside the existing `approvalRequired` / `policyRef` signals.
- On human approval, the shared `IOperationGateway.ApplyApprovedProposalAsync`
  resolves the executor by `OperationClass.Geoprocess`
  (`GeoprocessOperationExecutor`), which deserializes the payload and calls
  `IGeoprocessingJobService.ResumeApprovedJobAsync`. The resume re-submits the
  plan through the normal job pipeline with the approval and mutating-process
  gates **bypassed** (they were satisfied at proposal-creation time), attributing
  the job to the **original submitter** recorded in the payload — not the
  approver.

The status projection is therefore `Validated → AwaitingApproval → Submitted`,
mirroring the control-plane deploy/metadata-release proposals.

### Scope boundaries

- **Custom-code submissions are never persisted as a proposal.** The resume path
  cannot re-mint a custom-code scoped callback token without the live principal,
  so a gated custom-code submission continues to hard-fail. Custom-code trust is
  governed separately ([ADR-0063](0063-custom-code-execution-is-aws-batch-only.md)).
- **When the durable proposal surface is unavailable** (lightweight/Redis-free
  hosts where no `IOperationGateway` is registered), submission hard-fails exactly
  as before — the classifier still fails closed; there is simply no resume record.
- **Feature-data editing via MCP stays forbidden**
  ([ADR-0028](0028-ai-data-editing-not-allowed.md)). This lane governs GP *process
  plans* only; it does not open a feature-edit path.
- The GP lane does not add `Geoprocess` to the `honua_propose_operation` tool
  schema — GP proposals are created by the GP submit path, not by the
  control-plane propose tool.

## Consequences

- A destructive/sink GP plan is now recoverable: propose → poll
  `honua://proposals/{id}` → approve → resume, with no re-submission from scratch.
- A newly-registered mutating process is safe by default; forgetting to extend a
  denylist can no longer open an ungated execution path.
- The GP lane depends only on the Core `IOperationGateway` /
  `IOperationProposalStore` abstractions; the executor lives in
  `Honua.Geoprocessing` and resolves the job service lazily to avoid the
  gateway ↔ job-service construction cycle.
- Because the GP gate — not the edition ladder — decides GP approval, the lane
  uses `CreateApprovalProposalAsync` rather than `RouteAsync`; the edition ladder
  is intentionally not the decision authority for GP destructiveness.

## References

- honua-server#2814 — GP approval lane + fail-closed destructive classifier.
- [ADR-0029](0029-geoprocess-canonical-model-mappings.md) — canonical destructive
  routing decision this ADR extends.
- [ADR-0056](0056-mcp-redesign-unified-governed-surface.md),
  [ADR-0028](0028-ai-data-editing-not-allowed.md),
  [ADR-0063](0063-custom-code-execution-is-aws-batch-only.md).
