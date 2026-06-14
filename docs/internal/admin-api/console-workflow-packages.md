# Console Workflow Packages API

Ticket #1185 adds the server-owned workflow package contract for the Console
GP/ETL editor. The API is rooted at `/api/v1/console`; paths below are relative
to that root. It requires the standard admin authorization posture, returns
`ApiResponse<T>` envelopes for successful workflow responses, uses
source-generated JSON, and maps operational failures through the admin
ProblemDetails helpers.

All workflow package endpoints set `Cache-Control: no-store`. The node registry
also returns an `ETag` whose value is the registry snapshot version.

## Response And JSON Contract

Workflow-package JSON uses lower-camel-case property names, string enum values
such as `Geoprocessing`, `Data`, `Job`, and `Active`, and omits null values.
Graph node parameters and run parameters are `Dictionary<string,string>` payloads
so the transport remains source-generated and AOT-safe.

Workflow responses use this envelope shape:

```json
{
  "success": true,
  "data": {},
  "message": null,
  "timestamp": "2026-05-25T00:00:00Z"
}
```

Validation failures from version creation or publication return HTTP `400` with
`success=false`, `message="Workflow package validation failed."`, and `data`
set to `WorkflowPackageValidationResult`. Route mismatches, unknown packages,
disabled publications, duplicate publication ids, authorization, admission,
approval, and store/runtime failures use the shared admin ProblemDetails
response shape.

## Node Registry

`GET /workflow-node-registry` returns the palette the Console renders:

- `registryVersion`, `generatedAt`, `providers`, `nodes`, and registry-level
  `warnings`
- Node identity fields: `nodeTypeId`, `providerId`, `runtimeKind`, `title`,
  `description`, `category`, and optional `processId`
- `parameterSchemas` for form rendering and server validation
- `inputSchemas` and `outputSchemas` for graph wiring
- `capabilityFlags` for validate, dry run, job, schedule, process-endpoint, and
  executable support
- `runtimeHints` for worker profile, estimated duration, cost weight, and cost
  unit
- `eligibilityWarnings` for non-fatal provider or target caveats

`GET /workflow-node-registry/{nodeTypeId}` returns one node definition or
ProblemDetails `404` when the node type is unknown. Clients should URL-encode
node type ids in path segments because process-backed ids contain `:`, for
example `/workflow-node-registry/process%3Ageometry.buffer`.

The first provider adapts the built-in geoprocessing process catalog as
`process:{processId}` nodes. These nodes use runtime kind `Geoprocessing`,
provider id `geoprocessing.process-catalog`, worker profile `geoprocessing`, and
currently advertise validate, dry-run, job, schedule, process-endpoint, and
executable support. GeoETL-specific executable node providers remain follow-on
work; clients should rely on `capabilityFlags` and provider warnings instead of
hard-coding provider assumptions.

## Package Lifecycle

Workflow package drafts are mutable. Versions are immutable snapshots created
from the current draft:

| Method | Path | Response |
|---|---|---|
| `GET` | `/workflow-packages` | `200 ApiResponse<WorkflowPackageListResponse>` |
| `POST` | `/workflow-packages` | `201 ApiResponse<WorkflowPackage>` |
| `GET` | `/workflow-packages/{packageId}` | `200 ApiResponse<WorkflowPackage>` or ProblemDetails `404` |
| `PUT` | `/workflow-packages/{packageId}` | `200 ApiResponse<WorkflowPackage>` or ProblemDetails `400`/`404` |
| `GET` | `/workflow-packages/{packageId}/versions` | `200 ApiResponse<WorkflowPackageVersionListResponse>` |
| `POST` | `/workflow-packages/{packageId}/versions` | `201 ApiResponse<WorkflowPackageVersion>` or `400 ApiResponse<WorkflowPackageValidationResult>` |
| `GET` | `/workflow-packages/{packageId}/versions/{packageVersion}` | `200 ApiResponse<WorkflowPackageVersion>` or ProblemDetails `404` |

`POST /workflow-packages` creates a generated package id when `packageId` is
omitted and replaces the draft when a provided id already exists. `PUT` requires
an existing draft and rejects a body `packageId` that differs from the route.
Draft save validates only the presence of name and graph; graph validation runs
when a version is created, validated, dry-run, or published.

The current default `IWorkflowPackageStore` registration is in-memory. It
establishes the contract and integration path, but draft, version, and
publication records are not durable across process restarts until a persistent
store is wired in a follow-on change.

## Graph Contract

`WorkflowGraph.schemaVersion` defaults to `workflow-package.v1`. Graph nodes
reference server registry node types and carry parameter values as strings for
AOT-safe transport:

```json
{
  "nodes": [
    {
      "nodeId": "area-1",
      "nodeTypeId": "process:geometry.area",
      "parameters": {
        "wkb": "AQ==",
        "srid": "4326"
      }
    }
  ],
  "edges": []
}
```

Validation checks empty graphs, duplicate or empty node ids, unknown node types,
non-executable node types, missing required parameters, unknown edge endpoints,
self edges, and dependency cycles. A required parameter is satisfied either by a
literal value or by an incoming `Data` edge whose `targetPort` names it, so a
downstream input wired from an upstream output is not reported as missing.
Failure edges are excluded from cycle checks; a failure edge without `targetPort`
produces a warning because it is treated as a control-only failure branch.

`Data` edges carry an optional `sourcePort` (the upstream output) and a
`targetPort` (the downstream input key). When compiled for scheduled execution,
each such edge becomes a step input binding that resolves the upstream artifact
(`artifact:{sourcePort}`, or the first artifact when `sourcePort` is omitted)
into the downstream step input at run time. Static package validation still
checks literal parameter values, unknown parameters, and supported processes, but
the type check for a data-bound input is deferred until the upstream artifact is
resolved.

The package hash is computed from the graph schema version, graph worker
profile, node ids, node types, node worker profiles, node parameters, and edge
topology/ports. Package metadata, node metadata, editor metadata, and schedule
declarations do not affect the hash.

## Validation And Dry Run

Stored versions can be validated and dry-run:

| Method | Path | Response |
|---|---|---|
| `POST` | `/workflow-packages/{packageId}/versions/{packageVersion}/validate` | `200 ApiResponse<WorkflowPackageValidationResult>` |
| `POST` | `/workflow-packages/{packageId}/versions/{packageVersion}/dry-run` | `200 ApiResponse<WorkflowDryRunResult>` |

Version validation combines graph structure checks, publication-target
eligibility when applicable, authorization to execute process work, and the
canonical geoprocessing plan validator for process-backed nodes.

Dry runs return validation state, estimated duration, estimated cost weight,
output schemas, log entries, preview artifact metadata, truncation state, and
`packageHash`. If validation fails, the dry-run response remains `200` with
`validation.isValid=false`, a warning log entry, and no execution preview. Valid
process-backed packages delegate bounded estimation to
`IGeoprocessingJobService.DryRunPlan` when all required inputs are literal. For
data-wired graphs, dry run returns the validated graph-level estimate and
preview artifact metadata without resolving upstream artifacts.

Authorization, store-unavailable, admission, and approval failures use the same
admin ProblemDetails status mapping as the geoprocessing runtime (`401`/`403`,
`503`, `429`, and `409` respectively).

## Publication And Runs

Stored versions can be published and run:

| Method | Path | Response |
|---|---|---|
| `POST` | `/workflow-packages/{packageId}/versions/{packageVersion}/publish` | `200 ApiResponse<WorkflowPublication>`, `400 ApiResponse<WorkflowPackageValidationResult>`, or ProblemDetails `409` |
| `GET` | `/workflow-publications` | `200 ApiResponse<WorkflowPublicationListResponse>` |
| `POST` | `/workflow-publications/{publicationId}/runs` | `201 ApiResponse<WorkflowPublicationRunResult>` or ProblemDetails `404`/`409` |

Publication targets are `Job`, `Schedule`, and `ProcessEndpoint`.

- `Job` publications run through the durable geoprocessing job substrate.
- `Schedule` publications require a schedule declaration either in the publish
  request or on the graph. When `IWorkflowDefinitionStore` is registered, the
  service compiles a workflow definition using the existing orchestration
  contract, materializing each data edge as a `StepInputBinding` so upstream
  outputs flow into downstream inputs. Manual runs create orchestration runs
  only when `WorkflowOrchestrationEngine` is available; otherwise they fall back
  to a geoprocessing job run.
- `ProcessEndpoint` publications record an eligible process identifier and an
  `endpointPath` for later catalog integration. They do not create a public OGC
  API Processes or GPServer endpoint in this slice.

`WorkflowPublication` responses include `publicationId`, `packageId`,
`packageVersion`, `packageHash`, `target`, `status`, `endpointPath`,
`eligibility`, `createdAt`, `createdBy`, and stamped `provenance`. Schedule
publications also include `schedule` and `workflowDefinitionId`; process-endpoint
publications include `processId`.

Re-using an existing `publicationId` is rejected with ProblemDetails `409`;
publications are immutable run targets and are never silently retargeted.

`Job` and `ProcessEndpoint` publications compile to a single dispatched
geoprocessing job, which cannot resolve cross-node data bindings. Publishing a
graph that wires data between nodes (a `Data` edge with a `targetPort`) to those
targets fails eligibility with `UNSUPPORTED_DATA_BINDING_TARGET`; such graphs
must publish to a `Schedule` target so the orchestration engine can chain
upstream outputs into downstream inputs.

Disabled publications return ProblemDetails `409` when a run is requested.
`RunWorkflowPublicationRequest.parameters` are merged into run provenance; they
do not rewrite the immutable graph parameters captured in the package version,
and caller-supplied values for the reserved `workflow.*` provenance keys below
are ignored so traceability cannot be spoofed.

Run responses include either `jobId` or `workflowRunId`. The `Location` header
points at `/api/v1/admin/jobs/{jobId}` for job-backed runs and
`/api/v1/admin/operations/{workflowRunId}` for orchestration-backed runs.

## Provenance

Publications and runs stamp these reserved metadata keys into created execution
jobs or workflow runs:

- `workflow.packageId`
- `workflow.packageVersion`
- `workflow.publicationId`
- `workflow.packageHash`
- `workflow.publicationTarget`
- `workflow.processId` when the publication target is `ProcessEndpoint`

This keeps job, run, and provenance records traceable to the exact immutable
package version that produced them. These keys are stamped server-side and are
treated as reserved: caller-supplied run parameters that use one of them are
ignored rather than allowed to overwrite the stamped value.
