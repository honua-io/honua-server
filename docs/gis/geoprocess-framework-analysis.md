# Geoprocess Framework Comparative Analysis

**Ticket:** honua-io/honua-server#360
**Date:** 2026-04-12
**Status:** Active reference — constrains #721, #723, #529, #724

This document compares Esri GPServer, GeoServer process exposure patterns, and
OGC API Processes semantics, then maps each to the Honua canonical process model.

The decision record for why Honua chose its canonical nouns lives in
[ADR-0029](../contributor/adr/0029-geoprocess-canonical-model-mappings.md).

## Frameworks Compared

| Aspect | Esri GPServer | GeoServer (WPS / OGC API) | OGC API Processes Part 1 |
| --- | --- | --- | --- |
| Specification owner | Esri (proprietary REST spec) | OGC WPS 1.0/2.0 + OGC API Processes | OGC |
| Transport | REST (JSON / form-encoded) | XML (WPS), JSON (OGC API Processes) | REST (JSON) |
| Discovery | Service root lists tasks | GetCapabilities / `GET /processes` | `GET /processes` |
| Process description | Task parameters at `/{task}` | DescribeProcess / `GET /processes/{id}` | `GET /processes/{processId}` |
| Sync execution | `POST /{task}/execute` | WPS Execute (sync) / `POST /processes/{id}/execution` | `POST /processes/{id}/execution` (200) |
| Async execution | `POST /{task}/submitJob` | WPS Execute (async) / `POST /processes/{id}/execution` + `Prefer: respond-async` | `POST /processes/{id}/execution` + `Prefer: respond-async` (201 + Location) |
| Job status | `GET /{task}/jobs/{jobId}` | GetStatus / `GET /jobs/{jobId}` | `GET /jobs/{jobId}` |
| Result retrieval | `GET /{task}/jobs/{jobId}/results/{paramName}` (per-output) | GetResult / `GET /jobs/{jobId}/results` | `GET /jobs/{jobId}/results` (format depends on response mode) |
| Cancellation | `POST /{task}/jobs/{jobId}/cancel` | Dismiss / `DELETE /jobs/{jobId}` | `DELETE /jobs/{jobId}` |
| Parameter typing | Esri GP types (GPString, GPFeatureRecordSetLayer, etc.) | WPS LiteralData/ComplexData/BoundingBoxData | JSON Schema |

## Process Discovery

### Esri GPServer

The service root (`/rest/services/{folder}/{name}/GPServer`) returns a list of
tasks. Each task is a named geoprocessing tool with typed input/output
parameters and an execution type (synchronous or asynchronous). Discovery is
scoped to a single published service; there is no cross-service process
registry.

### GeoServer

GeoServer exposes processes via WPS GetCapabilities (XML) or the newer OGC API
Processes endpoint (`/processes`). GeoServer processes are registered through
Java process plugins and organized into process groups with security
integration. Discovery returns all processes visible to the caller.

### OGC API Processes Part 1

`GET /processes` returns a list of process summaries. `GET /processes/{processId}`
returns a full description including JSON Schema for inputs and outputs,
supported job control options (`sync-execute`, `async-execute`, `dismiss`), and
output transmission modes (`value`, `reference`).

### Honua Mapping

Honua does not expose a flat list of tasks or processes at the protocol adapter
level of its canonical model. Instead:

- `CatalogService` handles process discovery (process definitions, capabilities)
- `ProcessService` handles validation, execution, and job lifecycle
- `AnalysisPlan` is a multi-step DAG that may compose multiple process
  invocations — a concept absent from both GPServer and OGC API Processes

Protocol adapters project the catalog and process service into the expected
discovery shape for each protocol.

## Parameter Semantics

### Esri GPServer

Parameters use Esri-specific GP types:

| GP Type | Description |
| --- | --- |
| GPString, GPDouble, GPLong, GPBoolean, GPDate | Scalar literals |
| GPLinearUnit, GPArealUnit | Unit-qualified scalars |
| GPFeatureRecordSetLayer | Feature input/output (inline GeoJSON-like or reference) |
| GPRecordSet | Tabular input/output |
| GPRasterDataLayer, GPRasterData | Raster input/output |
| GPDataFile | Arbitrary file reference |
| GPMultiValue:\<type\> | Repeatable parameter wrapper |

Parameters have direction (input/output), requirement level (required/optional/
derived), default values, and optional choice lists. The `derived` type
indicates server-computed outputs not supplied by the caller.

### OGC API Processes

Inputs and outputs are described using JSON Schema. No type system is imposed
beyond what JSON Schema provides. Complex geospatial inputs use GeoJSON or
reference URIs. The specification does not prescribe unit-qualified types.

### GeoServer WPS

WPS uses LiteralData, ComplexData (with MIME types), and BoundingBoxData. The
GeoServer PPIO (Process Parameter I/O) framework handles serialization between
Java types and wire formats. This is GeoServer-internal and not part of the OGC
standard.

### Honua Mapping

Honua uses `AnalysisPlanStep.Inputs` as an opaque `IReadOnlyDictionary<string, string>`
for step-specific parameters. The canonical model deliberately avoids adopting
either Esri GP types or JSON Schema as its internal parameter representation.

Protocol adapters are responsible for:

- GPServer adapter (#723): translating Esri GP types to/from step inputs
- OGC API Processes adapter (#529): translating JSON Schema inputs to/from step inputs

The canonical `ArtifactKind` enum (Scalar, FeatureLayer, Table, Raster, File,
Report, Map, AppBundle) provides transport-neutral output typing that both
adapters can project into their protocol-specific type systems.

## Execution Modes

### Esri GPServer

Each task declares a fixed execution type: `esriExecutionTypeSynchronous` or
`esriExecutionTypeAsynchronous`. A synchronous task exposes only `/execute`; an
asynchronous task exposes only `/submitJob`. Some services publish a task in
both modes under different names.

### OGC API Processes

Execution mode is negotiated per-request. A process declares supported
`jobControlOptions` (`sync-execute`, `async-execute`, `dismiss`). The client
uses the `Prefer: respond-async` header to request asynchronous execution. The
server may honor or ignore the preference.

### GeoServer

WPS supports both modes. GeoServer determines execution strategy based on
process annotations and server configuration.

### Honua Mapping

Honua's canonical model supports both modes:

- **Synchronous**: `ProcessService.ExecutePlan` (currently stubbed, returns
  `Unimplemented` — to be completed in #721)
- **Asynchronous**: `ProcessService.SubmitPlanJob` → `ExecutionJob` with status
  polling via `GetJob`. The durable job orchestration substrate
  ([ADR-0031](../contributor/adr/0031-durable-job-orchestration-substrate.md))
  defines the claim/heartbeat/retry/cancellation contracts that this path will
  use once wired end-to-end (follow-on: #721).

The canonical model does not force a per-process execution type. Both modes
operate on the same `AnalysisPlan`. The protocol adapters determine how to
expose mode selection:

- GPServer adapter (#723): expose sync/async as separate task endpoints per Esri
  convention, or use the `executionType` field
- OGC API Processes adapter (#529): honor `Prefer: respond-async` header and
  report `jobControlOptions` in process descriptions

## Job Lifecycle States

### Comparative State Matrix

| Honua `ExecutionJobStatus` | Esri GPServer `jobStatus` | OGC API Processes `status` | Notes |
| --- | --- | --- | --- |
| `Queued` | `esriJobSubmitted`, `esriJobNew` | `accepted` | Honua collapses Esri's new/submitted distinction |
| `Provisioning` | `esriJobWaiting` | `accepted` | OGC has no provisioning concept; adapters report `accepted` |
| `Running` | `esriJobExecuting` | `running` | Direct equivalence |
| `Succeeded` | `esriJobSucceeded` | `successful` | Direct equivalence (note OGC spells it "successful") |
| `Failed` | `esriJobFailed`, `esriJobTimedOut` | `failed` | Honua collapses timeout into failure with error metadata |
| `Cancelled` | `esriJobCancelled` | `dismissed` | OGC uses "dismissed" for both cancellation and cleanup |
| — | `esriJobCancelling` | — | Transient Esri state; Honua does not expose a cancelling state (cancellation is best-effort) |
| — | `esriJobDeleting`, `esriJobDeleted` | — | **Deliberately excluded.** Honua uses workspace lifecycle for resource cleanup, not job-level deletion states |

### Honua `GeoprocessingWorkflowStatus` (Extended Lifecycle)

The canonical model also tracks a richer workflow lifecycle via
`GeoprocessingWorkflowStatus` that has no direct equivalent in GPServer or OGC
API Processes:

| Status | Purpose | Protocol Adapter Exposure |
| --- | --- | --- |
| `Draft` | Plan is being constructed | Not exposed — internal to operator workflow |
| `AwaitingClarification` | Missing or ambiguous inputs need user resolution | Not exposed — internal to operator workflow |
| `Validated` | Plan has passed validation | Not exposed — internal to operator workflow |
| `AwaitingApproval` | Policy or human gate before execution | Not exposed — internal to operator workflow |
| `AwaitingExecution` | Approved, queued for execution | Adapters map to Queued/accepted |
| `Running` | Executing | Adapters map to Running/running |
| `Completed` | Finished successfully | Adapters map to Succeeded/successful |
| `Failed` | Finished with errors | Adapters map to Failed/failed |
| `Cancelled` | User-initiated cancellation | Adapters map to Cancelled/dismissed |

Protocol adapters expose only the subset of states that their protocol defines.
The extended workflow states (`Draft`, `AwaitingClarification`, `Validated`,
`AwaitingApproval`) are internal to the operator workflow today. They are
tracked through the progress/admin surfaces (e.g., `IUniversalProgressStore`,
admin operations endpoints) but are not currently exposed through the gRPC
`ProcessService` or MCP. Future gRPC or operator-MCP exposure of these states
is follow-on contract work. Compatibility adapters never expose them.

## Result Retrieval

### Esri GPServer

Results are accessed per-output-parameter: `GET /{task}/jobs/{jobId}/results/{paramName}`.
Each output returns independently. The response includes the parameter value
and data type. There is no single envelope for all outputs.

### OGC API Processes

`GET /jobs/{jobId}/results` retrieves outputs, but the response format depends
on the original execute request. OGC API Processes Part 1 Core (§7.13, Table 12)
defines behavior based on the `response` parameter (`document` or `raw`) and the
`transmissionMode` per output (`value` or `reference`):

- **Document mode** (`response=document`): returns a JSON object containing all
  outputs keyed by output identifier. Each output may be inline (`value`) or a
  URI (`reference`).
- **Raw mode** (`response=raw`): for a single output, returns the output value
  directly with the appropriate media type. For multiple outputs, may return a
  multipart response or `204 No Content` with `Link` headers pointing to
  individual output resources.

Job status is reported on `GET /jobs/{jobId}` (§7.12), not within the results
payload.

### GeoServer WPS

GetResult returns all outputs. Complex outputs can be returned inline or by
reference (stored on the server with a retrieval URL).

### Honua Mapping

Honua uses `AnalysisResultPackage` as the canonical result envelope:

| `AnalysisResultPackage` Field | GPServer Projection | OGC Projection |
| --- | --- | --- |
| `ResultPackageId` | — (no equivalent) | — (no equivalent) |
| `Status` | Terminal result metadata only — records the final `GeoprocessingWorkflowStatus` at completion. Live job status comes from `ExecutionJobRecord.Status` via `GetJob` (see [GPServer adapter mapping](#gpserver-adapter-honua-server723)) | Not projected through `/results`; serve OGC `status` from `ExecutionJobRecord.Status` on `GET /jobs/{jobId}` |
| `Summary` (title, description) | Map to job messages | Deferred / non-standard for OGC Part 1 Core results; not included in the v1 `/results` payload |
| `Assumptions` | — (not in GPServer) | — (not in OGC) |
| `Artifacts` (list of `ArtifactRef`) | Project each artifact as a result parameter keyed by a stable output identifier (see adapter note below) | V1: project all artifacts as a document-mode JSON results object keyed by output identifier (see §7.13 subset below) |
| `WorkspaceRefs` | — (not in GPServer) | — (not in OGC) |
| `MapPackageId` | — (Honua-specific) | — (Honua-specific) |
| `AppPackageId` | — (Honua-specific) | — (Honua-specific) |
| `Provenance` | — (not in GPServer) | — (not in OGC) |
| `Errors` | Map to job messages with error type | Not projected through `/results`; failures surface through OGC exception responses and `GET /jobs/{jobId}` status |

The critical adapter difference is result access pattern:

- **GPServer adapter** (#723): must project each `ArtifactRef` as an
  individually addressable result at `/results/{paramName}`, matching
  GPServer's per-parameter result model. The route key must be a stable
  output identifier, not `ArtifactRef.Label` (which is human-readable).
  Adapters should establish a binding between the process definition's
  output parameter name and the artifact, using `ArtifactRef.Metadata`
  with a well-known key or a follow-on field addition to `ArtifactRef`
- **OGC adapter** (#529): v1 targets document-mode, by-value results — a single
  `/jobs/{jobId}/results` JSON response keyed by output identifier. V1 currently
  stubs the endpoint (successful jobs return `404` until the execution engine
  populates result storage). Raw-mode responses, reference-based transmission,
  and multipart output are deferred (see [Deliberately Excluded Behaviors](#from-ogc-api-processes))

## Cancellation Semantics

| Protocol | Mechanism | Behavior |
| --- | --- | --- |
| Esri GPServer | `POST /{task}/jobs/{jobId}/cancel` | Transitions through `esriJobCancelling` to `esriJobCancelled` |
| OGC API Processes | `DELETE /jobs/{jobId}` | Transitions to `dismissed`; may also delete the job resource |
| Honua canonical | `ProcessService.CancelJob` | Sets `ExecutionJobStatus.Cancelled` via `IJobCancellationNotifier`; best-effort, no transient cancelling state |

Adapter notes:

- GPServer adapter (#723): accepts both GET and POST to cancel and returns the
  current job status; does not synthesize `esriJobCancelling` — cancellation is
  best-effort and the response reflects the post-cancellation state
- OGC adapter: should accept DELETE and return 200 with the dismissed job
  status; if the protocol expects the job resource to be removed after
  dismissal, the adapter can return 404 on subsequent GET requests while the
  canonical job record remains in the store for auditing

## Deliberately Excluded Behaviors

### From Esri GPServer

| Behavior | Reason for Exclusion |
| --- | --- |
| `esriJobDeleting` / `esriJobDeleted` states | Honua manages resource cleanup through workspace lifecycle and retention policies, not job-level deletion states. The separation keeps job audit history stable. |
| Per-task fixed execution type | Honua determines execution mode per-request rather than per-process-definition. Adapters synthesize the Esri convention. |
| GP parameter type system as internal types | Esri GP types (GPString, GPFeatureRecordSetLayer, etc.) are protocol-specific. Honua uses transport-neutral `ArtifactKind` and opaque step inputs. |
| Result message stream | Esri jobs accumulate messages (informative, warning, error). Honua tracks progress via `GeoprocessingProgress` through `IUniversalProgressStore`, not per-job message lists. Adapters project progress events as job messages. |
| Service publishing model | GPServer is tightly coupled to ArcGIS Server service publishing. Honua process registration goes through `CatalogService`. |

### From GeoServer

| Behavior | Reason for Exclusion |
| --- | --- |
| WPS XML encoding | XML transport is not supported. OGC API Processes (JSON) supersedes WPS for Honua's purposes. |
| PPIO (Process Parameter I/O) framework | GeoServer-internal serialization concern. Not relevant to Honua's architecture. |
| Process groups with security integration | Honua handles authorization through `IOperatorAuthorizationEvaluator` and `IOperatorApprovalEvaluator` at the service layer, not process-group-level ACLs. |
| Process chaining (WPS) | Honua supports step composition natively through `AnalysisPlan` DAGs. WPS-style chaining where one process output feeds another inline is subsumed by plan step dependencies. For multi-plan workflow composition (chaining separate plan executions with artifact-binding and retry/failure policies), see the declarative workflow orchestration layer ([ADR-0032](../contributor/adr/0032-workflow-orchestration-layer.md)). |

### From OGC API Processes

| Behavior | Reason for Exclusion |
| --- | --- |
| Response mode negotiation (`document` vs `raw`) | Deferred. V1 accepts only document-mode execution requests; the planned successful results shape is a single JSON object with all outputs. The current implementation does not yet emit successful results documents. Raw-mode responses (direct media type, multipart, or `204` with `Link` headers) can be added when single-output streaming or large-result downloads are needed. |
| Output transmission negotiation (`value` vs `reference`) | Deferred. When successful results are populated, V1 returns them by value. Reference-based output can be added when large-result streaming is needed. |
| `Prefer: return=minimal` / `return=representation` | Deferred. V1 returns full job representation on creation. |
| Nested process execution (Part 2: Deploy, Replace, Undeploy) | Out of scope. Honua does not support user-deployed process definitions in v1. |
| Callback/subscriber notification (`Prefer: respond-async; callback=...`) | Deferred. V1 uses polling. Webhook/callback notification can be added later. |

## Honua Canonical Noun Reference

These are the transport-neutral nouns that the canonical model stabilizes.
Protocol adapters map to/from these nouns without inventing new internal
semantics.

| Canonical Noun | Domain Type | Purpose |
| --- | --- | --- |
| Process definition | `ProcessDefinition` (with `ProcessParameterSpec` / `ProcessParameterValueType`) discoverable via `IProcessCatalog` | Discoverable unit of geoprocessing capability; the built-in catalog seeds 14 geometry and analytics processes referenced by `AnalysisPlanStep.ProcessId` |
| Analysis intent | `AnalysisIntent` | Natural-language or structured goal before planning |
| Analysis plan | `AnalysisPlan` | Executable DAG of steps compiled from a grounded intent |
| Plan step | `AnalysisPlanStep` | One unit of work in the execution DAG |
| Execution job | `ExecutionJobRecord` | Durable record for a running or completed plan execution |
| Workspace | `Workspace` | Managed working-state container with lifecycle |
| Artifact | `Artifact` / `ArtifactRef` | Materialized output within a workspace |
| Result package | `AnalysisResultPackage` | Final result envelope with artifacts, provenance, and optional map/app outputs |
| Provenance record | `ProvenanceRecord` | Lineage and assumption tracking for a result |
| Progress | `GeoprocessingProgress` | Observable execution progress via `IUniversalProgressStore` |

## Protocol Adapter Mapping Summary

### GPServer Adapter (honua-server#723)

The GPServer adapter projects the Honua canonical model into Esri's GPServer
REST contract. Key mappings:

| GPServer Concept | Honua Source |
| --- | --- |
| Service root / task list | `CatalogService` process definitions |
| Task parameters | Process definition inputs → Esri GP type descriptors |
| `/{task}/execute` | `ProcessService.ExecutePlan` (sync) |
| `/{task}/submitJob` | `ProcessService.SubmitPlanJob` (async) |
| `/{task}/jobs/{jobId}` | `ProcessService.GetJob` → `ExecutionJobRecord` |
| `/{task}/jobs/{jobId}/results/{paramName}` | `ProcessService.GetJobResults` → individual `ArtifactRef` from `AnalysisResultPackage.Artifacts` |
| `/{task}/jobs/{jobId}/cancel` | `ProcessService.CancelJob` |
| Job status values | `ExecutionJobStatus` → Esri status string (see state matrix) |
| Job messages | `GeoprocessingProgress` events + `AnalysisResultPackage.Errors` |

Adapter invariants:

1. The adapter must not add lifecycle states beyond what `ExecutionJobStatus` provides
2. Parameter type translation is the adapter's responsibility — the canonical model does not store Esri GP types
3. Per-output result access must decompose `AnalysisResultPackage.Artifacts` by a stable output identifier — not `ArtifactRef.Label` (which is human-readable). The adapter must define or consume a binding between the process definition output parameter name and each artifact (e.g., via a well-known `ArtifactRef.Metadata` key)
4. The adapter may synthesize `esriJobCancelling` as a transient state but must not persist it

### OGC API Processes Adapter (honua-server#529)

The OGC API Processes adapter projects the Honua canonical model into the OGC
API Processes Part 1 Core contract. Key mappings:

| OGC Concept | Honua Source / V1 implementation |
| --- | --- |
| `GET /processes` | V1 static single-process projection (`honua-geoprocessing`); the internal `IProcessCatalog` now enumerates 14 built-in processes, but per-process projection into the adapter surface is follow-on work |
| `GET /processes/{processId}` | V1 static process description with JSON Schema inputs/outputs for the canonical process stub |
| `POST /processes/{id}/execution` (sync) | Not implemented in V1; synchronous execution returns `501 Not Implemented` |
| `POST /processes/{id}/execution` (async) | Adapter validates plan structure, requires `Prefer: respond-async`, and creates durable `ExecutionJobRecord` + `GeoprocessingProgress` state |
| `GET /jobs/{jobId}` | `IExecutionJobStore` → `ExecutionJobRecord` projected to OGC `StatusInfo` |
| `GET /jobs/{jobId}/results` | V1 stub: non-terminal `404`, successful `404`, failed `500`, dismissed `410`. Planned successful shape: document-mode, by-value output map derived from `AnalysisResultPackage.Artifacts` only (no job status, summary, or error envelope in `/results`) |
| `DELETE /jobs/{jobId}` | `IJobCancellationNotifier` + durable job store cancellation mapping to OGC `dismissed` |
| Job status values | `ExecutionJobStatus` → OGC status string (see state matrix) |
| `jobControlOptions` | V1 fixed capability declaration for the canonical process stub: `async-execute`, `dismiss` |
| `Prefer: respond-async` | Required for execution; successful submissions return `201 Created` with `Location` and `Preference-Applied: respond-async` |

Adapter invariants:

1. The adapter must not add lifecycle states beyond what `ExecutionJobStatus` provides
2. Input/output schema translation to JSON Schema is the adapter's responsibility
3. V1 targets document-mode, by-value successful results only: the planned shape is a single JSON object keyed by stable output identifiers (not `ArtifactRef.Label`), not per-parameter. The current implementation still stubs `/results` until the execution engine populates result packages. Raw-mode and reference-based transmission are deferred
4. `DELETE /jobs/{jobId}` maps to cancellation, not resource deletion — the canonical job record persists

## Backlog Guidance

### honua-server#721 — Canonical Contract Work

This ticket builds the canonical process contract. It must:

- Preserve the nouns and lifecycle states defined in this analysis
- Implement `ProcessService.ExecutePlan` (currently stubbed)
- Ensure `AnalysisResultPackage` is populated by the execution engine
- Not introduce protocol-specific concepts into the canonical model

### honua-server#723 — GeoServices GPServer Adapter ✓

**Implemented.** The GPServer adapter is a protocol adapter that:

- Maps GPServer REST routes over canonical process service operations (`GPServerEndpoints.cs`)
- Translates Esri GP parameter types to/from canonical step inputs and `ArtifactKind` (`GPServerParameterTranslation.cs`)
- Maps `ExecutionJobStatus` to Esri job status strings per the state matrix above (`GPServerStatusMapping.cs`)
- Routes per-parameter result endpoints via the `geoservices.output_parameter` metadata key (route registered; actual output retrieval pending execution-engine/result-storage support)
- Persists route binding metadata (`gpserver.serviceId`, `gpserver.taskName`) at submit time and validates it on status/result/cancel to prevent cross-protocol job access
- Does not add internal domain types or lifecycle states

### honua-server#529 — OGC API Processes Adapter (Implemented)

This **protocol adapter** is implemented. The adapter:

- Implements OGC API Processes Part 1 Core routes that project canonical process service operations
- V1 exposes one static canonical process descriptor (`honua-geoprocessing`) in `/processes`; plan submissions are validated against the built-in `IProcessCatalog` (14 seeded geometry/analytics processes) so unknown process IDs and missing required parameters are rejected at the adapter boundary, but per-process projection into the OGC surface is follow-on work
- Translates JSON Schema process descriptions from the canonical process stub
- Validates canonical plan structure at the adapter boundary before durable job creation
- Maps `ExecutionJobStatus` to OGC job status strings per the state matrix above
- V1 is async-only: `Prefer: respond-async` is required, successful submissions return `201 Created` with `Location` and `Preference-Applied: respond-async`, and sync execution returns `501`
- V1: result storage is pending; the `/results` endpoint stubs `404` for non-terminal and successful jobs, `500` for failed jobs, and `410` for dismissed jobs (target: document-mode, by-value JSON; raw-mode and reference transmission deferred)
- Does not add internal domain types or lifecycle states

See [OGC API Processes Coverage](specifications/ogc-api-processes-coverage.md) for endpoint and conformance details.

### honua-server#724 — Workflow Orchestration Layer ✓

**Implemented.** The declarative workflow orchestration layer composes
canonical `AnalysisPlan` executions into multi-step, chained, scheduled, and
DAG-style workflows:

- `WorkflowDefinition` declares a step graph where each step wraps an
  `AnalysisPlan` with optional `DependsOn`, `InputBindings` (artifact
  selectors), `RetryPolicy`, `FailurePolicy`, and `TimeoutSeconds`
- `WorkflowRun` tracks durable run and per-step state with lease-based
  reconciliation over the ADR-0031 substrate
- `GeoprocessingWorkflowJobExecutor` adapts `IWorkflowJobExecutor` to the
  canonical `ProcessService`, stamping idempotency keys and protocol metadata
  on every child-job submission
- Cron-based scheduling via `WorkflowTrigger` with durable per-workflow
  cursors and atomic fire-time claims
- Redis-gated: stores and background services only register when
  `IConnectionMultiplexer` is available (no fallback; non-Redis deployments
  do not host the engine)
- Does not extend the canonical process or result-package model; workflows
  compose plans, they do not redefine them

See [ADR-0032](../contributor/adr/0032-workflow-orchestration-layer.md) for
design rationale and [Operations — Workflow Orchestration](../operator/operations.md)
for operator guidance.

### geospatial-grpc#6 — Public Execution Contract

This ticket defines the public gRPC process contract in the `geospatial-grpc`
repository. The existing `process_service.proto` in Honua's codebase serves as
the canonical typed execution surface. The public contract should align with or
be derived from this proto definition.

## Source Specifications

- [Esri GPServer REST API](https://developers.arcgis.com/rest/services-reference/enterprise/gp-service/)
- [Esri GP Task](https://developers.arcgis.com/rest/services-reference/enterprise/gp-task/)
- [Esri Submit Job](https://developers.arcgis.com/rest/services-reference/enterprise/submit-gp-job/)
- [OGC API — Processes — Part 1: Core (OGC 18-062r2)](https://docs.ogc.org/is/18-062r2/18-062r2.html)
- [OGC WPS 2.0 (OGC 14-065)](https://docs.ogc.org/is/14-065/14-065.html)
- [GeoServer WPS documentation](https://docs.geoserver.org/latest/en/user/services/wps/index.html)

## Related Documents

- [ADR-0029: Geoprocess Canonical Model Mappings](../contributor/adr/0029-geoprocess-canonical-model-mappings.md)
- [ADR-0026: AI-First Operator Contract](../contributor/adr/0026-ai-first-operator-contract.md)
- [GeoServices REST Parity](geoservices-rest-parity.md)
