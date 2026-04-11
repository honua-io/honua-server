# Deterministic Operator Workflow Results

**Status:** Draft  
**Date:** 2026-04-09  
**Scope:** Draft stage model and result objects for AI-first analyst and builder workflows

This document defines the deterministic workflow skeleton around Honua's
probabilistic planner.

## Design Intent

The workflow must make three things explicit:

1. what the model is allowed to propose
2. what the platform must validate deterministically
3. what result object is emitted at each stage

## Deterministic Workflow

```text
Receive Request
  -> Capture Intent
  -> Ground Candidates
  -> Clarify If Required
  -> Compile Plan
  -> Validate Plan
  -> Dry Run / Estimate
  -> Execute
  -> Compose Map
  -> Compose App (optional)
  -> Publish / Export (optional)
  -> Return Result Package
```

## Stage Model

| Stage | Purpose | Model role | Deterministic role | Result |
|---|---|---|---|---|
| `CaptureIntent` | Form partial user goal | interpret language | enforce schema shape | `IntentCaptureResult` |
| `GroundCandidates` | Find datasets, processes, templates | rank candidates | fetch catalog and permissions | `GroundingResult` |
| `Clarify` | Gather missing high-value inputs | draft questions | enforce policy for when clarification is required | `ClarificationResult` |
| `CompilePlan` | Build executable graph | propose steps | normalize structure | `PlanCompilationResult` |
| `ValidatePlan` | Check executability and safety | none | validate schema, capability, auth, policy | `PlanValidationResult` |
| `DryRun` | Estimate work and side effects | optional suggestions | compute estimates | `DryRunResult` |
| `Execute` | Produce artifacts | none | run plan through services and jobs | `ExecutionResult` |
| `ComposeMap` | Produce map deliverable | suggest style/layout | bind artifacts, render preview, package map | `MapCompositionResult` |
| `ComposeApp` | Produce app scaffold | suggest app structure | bind package, generate files, preview | `AppCompositionResult` |
| `Publish` | Persist or deploy output | optional recommendations | approvals, publication state machine | `PublicationResult` |

## Clarification Policy

Clarification is mandatory when one or more of the following are true:

- a required input is missing
- two or more candidate interpretations would produce materially different results
- the action is destructive
- the action publishes or shares results externally
- a policy boundary or permission check requires explicit user choice
- the confidence band is below the configured threshold

Clarification may be skipped when:

- a documented default exists
- the assumption is reversible and low-risk
- the assumption is recorded in the result package
- the workflow is running in draft or exploratory mode

> **Serialization note:** JSON examples below show canonical C# member names as
> identifiers. Actual wire-format serialization (casing, string vs numeric) is
> determined by each transport adapter (REST, gRPC, MCP). These examples
> illustrate the semantic contract, not a prescribed wire encoding.

## Stage Results

### IntentCaptureResult

```json
{
  "stage": "CaptureIntent",
  "status": "Completed",
  "intent": {},
  "missingFields": [
    "areaOfInterest"
  ]
}
```

### GroundingResult

```json
{
  "stage": "GroundCandidates",
  "status": "Completed",
  "datasetCandidates": [],
  "processCandidates": [],
  "templateCandidates": [],
  "confidenceBand": "medium"
}
```

### ClarificationResult

```json
{
  "stage": "Clarify",
  "status": "NeedsUserInput",
  "required": true,
  "reasonCodes": [
    "MissingRequiredInput"
  ],
  "questions": []
}
```

### PlanCompilationResult

```json
{
  "stage": "CompilePlan",
  "status": "Completed",
  "plan": {},
  "warnings": []
}
```

### PlanValidationResult

```json
{
  "stage": "ValidatePlan",
  "status": "Completed",
  "isExecutable": true,
  "requiresApproval": false,
  "violations": [],
  "warnings": []
}
```

### DryRunResult

```json
{
  "stage": "DryRun",
  "status": "Completed",
  "estimatedDurationSeconds": 45,
  "estimatedArtifacts": [
    "FeatureLayer",
    "Map"
  ],
  "sideEffects": []
}
```

### ExecutionResult

```json
{
  "stage": "Execute",
  "status": "Completed",
  "jobId": "job_123",
  "artifacts": [],
  "workspaceRefs": []
}
```

### MapCompositionResult

```json
{
  "stage": "ComposeMap",
  "status": "Completed",
  "mapPackage": {},
  "previewArtifactId": "artifact_preview_png"
}
```

### AppCompositionResult

```json
{
  "stage": "ComposeApp",
  "status": "Skipped",
  "reason": "not_requested"
}
```

### PublicationResult

```json
{
  "stage": "Publish",
  "status": "Completed",
  "deployment": {
    "deploymentId": "dep_123",
    "deploymentKind": "app_package",
    "hostingMode": "static_site",
    "routePrefix": "/apps/flood-risk-review",
    "publicUrl": "https://honua.example.com/apps/flood-risk-review",
    "publicationState": "published"
  }
}
```

## Final Workflow Result

The workflow should emit a final deterministic envelope.

Conceptual shape:

```json
{
  "workflowId": "wf_123",
  "status": "Completed",
  "stageResults": [],
  "resultPackage": {},
  "assumptions": [],
  "provenance": {}
}
```

## Status Vocabulary

Deterministic stage kinds (`GeoprocessingStageKind`):

- `CaptureIntent`
- `GroundCandidates`
- `Clarify`
- `CompilePlan`
- `ValidatePlan`
- `DryRun`
- `Execute`
- `ComposeMap`
- `ComposeApp`
- `Publish`

Recommended common stage statuses (`GeoprocessingStageStatus`):

- `Pending`
- `Completed`
- `NeedsUserInput`
- `Blocked`
- `Failed`
- `Skipped`
- `Cancelled`

Recommended workflow statuses (`GeoprocessingWorkflowStatus`):

- `Draft`
- `AwaitingClarification`
- `Validated`
- `AwaitingApproval`
- `Running`
- `Completed`
- `Failed`
- `Cancelled`

## Result Package Requirements

The final `AnalysisResultPackage` requires:

- `resultPackageId`
- `status`
- `summary`
- `provenance`

The following fields default to empty collections when not supplied:

- `assumptions`
- `artifacts`
- `workspaceRefs`
- `errors`

`mapPackageId` and `appPackageId` are deferred optional references whose package
types are defined in downstream tickets (#730, #731).

## Deterministic Rules

The platform must reject execution when any of the following are true:

- required inputs are unresolved
- the plan references unknown datasets or processes
- the plan violates authorization or policy
- the result package cannot bind required outputs
- the map package cannot be constructed for a workflow that promised map output

The platform may proceed with recorded assumptions only when policy allows it.

## Evaluation Guidance

Claude and Codex evaluations should score:

- whether clarification was asked when required
- whether unnecessary clarification was avoided
- whether the plan validated cleanly
- whether outputs matched the requested result types
- whether a usable `MapPackage` was produced
- whether provenance recorded assumptions and clarifications correctly

## Progress Tracking

Geoprocessing workflows report progress through the unified operation progress
interface (`IOperationProgress`). The `GeoprocessingProgress` type tracks:

- `workflowStatus` — lifecycle status mapped to the workflow status vocabulary
- `currentStage` — the active deterministic stage
- `currentStageStatus` — per-stage status from the stage status vocabulary
- `stepsCompleted` / `totalSteps` — plan step progress
- `percentComplete` — computed from step counts

The workflow status maps to the unified `OperationStatus`:

| Workflow status | Unified status |
|---|---|
| `Draft` | `Queued` |
| `AwaitingClarification`, `Validated`, `AwaitingApproval`, `Running` | `Processing` |
| `Completed` | `Completed` |
| `Failed` | `Failed` |
| `Cancelled` | `Cancelled` |

Progress is observable through the admin operations endpoints
(`/api/v{version}/admin/operations/`) and supports cancellation via
`ICancellableOperationProgress`.

## Related Documents

- [AI Operator Contract](AI_OPERATOR_CONTRACT.md)
- [AI-First Operator Architecture](../contributor/AI_OPERATOR_ARCHITECTURE.md)
- [MCP Server](MCP_SERVER.md)
