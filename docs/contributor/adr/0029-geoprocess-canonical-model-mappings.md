# ADR-0029: Geoprocess Canonical Model Mappings

## Status

Accepted

## Context

Honua needs to support both Esri GeoServices GPServer and OGC API Processes as
compatibility adapters projected from a single canonical process model (see
[ADR-0026](0026-ai-first-operator-contract.md)).

The existing canonical model — `AnalysisPlan`, `ExecutionJobRecord`,
`AnalysisResultPackage`, `Workspace`, `Artifact`, and supporting types — was
designed as a transport-neutral internal model. However, no formal decision
record establishes:

1. Which canonical nouns map to which protocol concepts
2. Which lifecycle states the canonical model exposes vs. hides from adapters
3. Which external behaviors Honua deliberately excludes
4. What invariants downstream adapter tickets must preserve

Without this decision record, adapter authors risk inventing ad-hoc internal
semantics or pulling protocol-specific concepts into the canonical layer.

The comparative analysis backing this ADR is in
[Geoprocess Framework Analysis](../../gis/geoprocess-framework-analysis.md).

## Decision

### Canonical Nouns Are Transport-Neutral

The following canonical nouns are the internal domain model. Protocol adapters
map to and from these nouns. Adapters must not introduce new domain types into
`Honua.Core`.

| Canonical Noun | Domain Type | Adapter Responsibility |
| --- | --- | --- |
| Process definition | `ProcessDefinition` registered in `IProcessCatalog` (seeded built-in catalog from honua-server#735) | Adapters project into protocol-specific discovery responses (GPServer task list, OGC process list) |
| Analysis plan | `AnalysisPlan` | Adapters construct plans from protocol-specific execution requests |
| Plan step | `AnalysisPlanStep` | Adapters translate protocol-specific parameters into opaque step inputs |
| Execution job | `ExecutionJobRecord` | Adapters project job status into protocol-specific status values |
| Workspace | `Workspace` | Not directly exposed through compatibility adapters |
| Artifact | `Artifact` / `ArtifactRef` | Adapters project artifacts as protocol-specific result types |
| Result package | `AnalysisResultPackage` | Adapters reshape the package into protocol-specific result responses |
| Provenance | `ProvenanceRecord` | Not exposed through compatibility adapters (Honua-specific) |
| Progress | `GeoprocessingProgress` | Adapters project progress into protocol-specific status messages |

### Lifecycle State Mapping Is Defined

The canonical model has two lifecycle enums. Adapters map from these; they do
not extend them.

**`ExecutionJobStatus`** (job-level, exposed to all adapters):

| Canonical | GPServer | OGC API Processes |
| --- | --- | --- |
| `Queued` | `esriJobSubmitted` | `accepted` |
| `Provisioning` | `esriJobWaiting` | `accepted` |
| `Running` | `esriJobExecuting` | `running` |
| `Succeeded` | `esriJobSucceeded` | `successful` |
| `Failed` | `esriJobFailed` | `failed` |
| `Cancelled` | `esriJobCancelled` | `dismissed` |

**`GeoprocessingWorkflowStatus`** (workflow-level, internal today):

The extended states (`Draft`, `AwaitingClarification`, `Validated`,
`AwaitingApproval`, `AwaitingExecution`) are Honua-specific. They are internal
to the operator workflow and tracked through progress/admin surfaces (e.g.,
`IUniversalProgressStore`, admin operations endpoints). They are not currently
exposed through the gRPC `ProcessService` or MCP — future exposure through a
dedicated progress/status RPC or operator MCP workflow surface is follow-on
contract work. These states predate the execution phase and have no equivalent
in GPServer or OGC API Processes.

### Result Access Patterns Differ By Protocol

The canonical `AnalysisResultPackage` is the single source of truth for results.
Adapters reshape it:

- **GPServer**: decompose `Artifacts` into per-parameter result endpoints
  (`/results/{paramName}`), matching Esri's per-output model. The route key
  must be a stable output identifier, not `ArtifactRef.Label` (which is
  human-readable). Adapters should bind artifacts to process definition
  output parameter names via `ArtifactRef.Metadata` or a follow-on field
- **OGC API Processes (v1 subset)**: target all `Artifacts` in a single
  `/jobs/{jobId}/results` JSON response using document-mode, by-value
  transmission. The `#529` implementation returns `200 OK` with the
  document-mode body on success (empty `{}` until the canonical process
  declares value-typed outputs and the execution engine populates result
  storage); failed jobs return `500`, and dismissed jobs return `410`. This
  is a Honua v1 adapter decision — the full OGC spec (§7.13) also supports
  raw-mode responses and reference-based transmission, which are deferred

### Parameter Translation Is An Adapter Concern

The canonical model uses `AnalysisPlanStep.Inputs` as opaque
`IReadOnlyDictionary<string, string>`. Protocol adapters own the translation
between:

- Esri GP types (GPString, GPFeatureRecordSetLayer, etc.) and step inputs
- JSON Schema inputs/outputs and step inputs

The canonical model does not store, validate, or depend on protocol-specific
parameter types.

### Excluded Behaviors Are Deliberate

The following behaviors from external frameworks are deliberately not adopted
into the canonical model:

1. **Esri job deletion states** (`esriJobDeleting`, `esriJobDeleted`): Honua
   uses workspace lifecycle and retention policies for resource cleanup. Job
   records are durable audit artifacts.

2. **Per-task fixed execution type**: Honua determines execution mode
   per-request. The GPServer adapter synthesizes the Esri convention.

3. **WPS XML encoding**: not supported. OGC API Processes (JSON) is the target
   OGC surface.

4. **GeoServer PPIO and process groups**: GeoServer-internal concerns with no
   relevance to Honua's architecture.

5. **OGC response mode negotiation** (`document` vs `raw`): deferred. V1
   supports only document-mode responses. Raw-mode responses (direct media type,
   multipart, `204` with `Link` headers) are out of scope.

6. **OGC output transmission negotiation** (`value` vs `reference`): deferred.
   When successful results are populated, V1 returns them by value.

7. **OGC callback notification**: deferred. V1 uses polling.

8. **OGC Processes Part 2** (Deploy, Replace, Undeploy): out of scope for v1.

### Downstream Ticket Constraints

**honua-server#721** (canonical contract): must preserve these nouns and lifecycle
states. Must not introduce protocol-specific concepts. Must implement
`ProcessService.ExecutePlan` and ensure `AnalysisResultPackage` is populated by
the execution engine.

**honua-server#723** (GPServer adapter): protocol adapter only. Must translate
between Esri GPServer REST conventions and the canonical model. Must not add
domain types to `Honua.Core`.

**honua-server#529** (OGC API Processes adapter): protocol adapter only. Must
translate between OGC API Processes Part 1 Core conventions and the canonical
model. Must not add domain types to `Honua.Core`.

## Consequences

### Positive

- Adapter authors have a clear mapping reference and do not need to make
  canonical model decisions.
- The canonical model remains transport-neutral and does not accumulate
  protocol-specific types.
- Both GPServer and OGC API Processes can be projected from the same internal
  semantics without conflict.
- Honua-specific capabilities (multi-step plans, provenance, map/app outputs,
  approval gates) are preserved without leaking into compatibility adapters.

### Negative

- Adapters must maintain non-trivial translation logic, especially for parameter
  types and result access patterns.
- The opaque `IReadOnlyDictionary<string, string>` parameter model may need
  richer typing as the process catalog grows. This is acceptable for v1 but may
  require a follow-up ADR.

### Follow-On Work

- honua-server#721: implement the canonical process contract per these constraints
- honua-server#723: implement the GPServer adapter per the mapping tables
- honua-server#529: OGC API Processes adapter — **implemented** (see [coverage](../../gis/specifications/ogc-api-processes-coverage.md))
- geospatial-grpc#6: align the public gRPC contract with the canonical
  `process_service.proto`
- Formalize `ProcessDefinition` as a first-class domain type — **implemented** in
  honua-server#735 and extended in honua-server#737. `ProcessDefinition`,
  `ProcessParameterSpec`, and `ProcessParameterValueType` live in
  `Honua.Core.Features.Geoprocessing.Domain`, and `IProcessCatalog` now seeds
  19 built-in processes across four categories (10 `geometry.*`, 4 `analytics.*`,
  2 `generalization.*`, 3 `data-management.*`) that plan validation checks
  `AnalysisPlanStep.ProcessId` against. Destructive `data-management.*` ids
  (`delete-features`, `calculate-field`) are classified server-side by
  `ProcessDestructiveClassifier` so submission and execution route the plan
  through `OperatorApprovalGate` with `IsDestructive = true` without adding a
  destruction flag to the canonical `ProcessDefinition`. Per-process projection
  into the GPServer and OGC API Processes adapter surfaces remains follow-on
  work
- Consider richer parameter typing when the opaque dictionary proves insufficient
