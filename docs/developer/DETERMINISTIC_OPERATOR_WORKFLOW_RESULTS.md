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
| `capture_intent` | Form partial user goal | interpret language | enforce schema shape | `IntentCaptureResult` |
| `ground_candidates` | Find datasets, processes, templates | rank candidates | fetch catalog and permissions | `GroundingResult` |
| `clarify` | Gather missing high-value inputs | draft questions | enforce policy for when clarification is required | `ClarificationResult` |
| `compile_plan` | Build executable graph | propose steps | normalize structure | `PlanCompilationResult` |
| `validate_plan` | Check executability and safety | none | validate schema, capability, auth, policy | `PlanValidationResult` |
| `dry_run` | Estimate work and side effects | optional suggestions | compute estimates | `DryRunResult` |
| `execute` | Produce artifacts | none | run plan through services and jobs | `ExecutionResult` |
| `compose_map` | Produce map deliverable | suggest style/layout | bind artifacts, render preview, package map | `MapCompositionResult` |
| `compose_app` | Produce app scaffold | suggest app structure | bind package, generate files, preview | `AppCompositionResult` |
| `publish` | Persist or deploy output | optional recommendations | approvals, publication state machine | `PublicationResult` |

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

## Stage Results

### IntentCaptureResult

```json
{
  "stage": "capture_intent",
  "status": "completed",
  "intent": {},
  "missingFields": [
    "aoi"
  ]
}
```

### GroundingResult

```json
{
  "stage": "ground_candidates",
  "status": "completed",
  "datasetCandidates": [],
  "processCandidates": [],
  "templateCandidates": [],
  "confidenceBand": "medium"
}
```

### ClarificationResult

```json
{
  "stage": "clarify",
  "status": "needs_user_input",
  "required": true,
  "reasonCodes": [
    "missing_required_input"
  ],
  "questions": []
}
```

### PlanCompilationResult

```json
{
  "stage": "compile_plan",
  "status": "completed",
  "plan": {},
  "warnings": []
}
```

### PlanValidationResult

```json
{
  "stage": "validate_plan",
  "status": "completed",
  "isExecutable": true,
  "requiresApproval": false,
  "violations": [],
  "warnings": []
}
```

### DryRunResult

```json
{
  "stage": "dry_run",
  "status": "completed",
  "estimatedDurationSeconds": 45,
  "estimatedArtifacts": [
    "feature_layer",
    "map_package"
  ],
  "sideEffects": []
}
```

### ExecutionResult

```json
{
  "stage": "execute",
  "status": "completed",
  "jobId": "job_123",
  "artifacts": [],
  "workspaceRefs": []
}
```

### MapCompositionResult

```json
{
  "stage": "compose_map",
  "status": "completed",
  "mapPackage": {},
  "previewArtifactId": "artifact_preview_png"
}
```

### AppCompositionResult

```json
{
  "stage": "compose_app",
  "status": "skipped",
  "reason": "not_requested"
}
```

### PublicationResult

```json
{
  "stage": "publish",
  "status": "completed",
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
  "status": "completed",
  "stageResults": [],
  "resultPackage": {},
  "assumptions": [],
  "provenance": {}
}
```

## Status Vocabulary

Recommended common stage statuses:

- `pending`
- `completed`
- `needs_user_input`
- `blocked`
- `failed`
- `skipped`
- `cancelled`

Recommended workflow statuses:

- `draft`
- `awaiting_clarification`
- `validated`
- `awaiting_approval`
- `running`
- `completed`
- `failed`
- `cancelled`

## Result Package Requirements

The final `AnalysisResultPackage` should require:

- `status`
- `summary`
- `assumptions`
- `artifacts`
- `workspaceRefs`
- `mapPackageId`
- `provenance`

`appPackageId` is optional but recommended for builder flows.

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
| `draft` | `queued` |
| `awaiting_clarification`, `validated`, `awaiting_approval`, `running` | `processing` |
| `completed` | `completed` |
| `failed` | `failed` |
| `cancelled` | `cancelled` |

Progress is observable through the admin operations endpoints
(`/api/v{version}/admin/operations/`) and supports cancellation via
`ICancellableOperationProgress`.

## Related Documents

- [AI Operator Contract](AI_OPERATOR_CONTRACT.md)
- [AI-First Operator Architecture](../contributor/AI_OPERATOR_ARCHITECTURE.md)
- [MCP Server](MCP_SERVER.md)
