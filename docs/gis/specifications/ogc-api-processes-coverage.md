# OGC API Processes Coverage (V1)

This page summarizes Honua V1 support for OGC API Processes Part 1 — Core.

Honua implements OGC API Processes as a **protocol adapter** over the canonical geoprocessing runtime. The adapter translates between OGC API Processes conventions and Honua's internal process model without adding protocol-specific domain types. See [ADR-0029](../../contributor/adr/0029-geoprocess-canonical-model-mappings.md) for the canonical model mapping and [Geoprocess Framework Analysis](../geoprocess-framework-analysis.md) for the cross-protocol comparison.

## Conformance Classes

| Conformance class | URI | Status |
|---|---|---|
| Core | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core` | Implemented |
| JSON | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json` | Implemented |
| Job List | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list` | MVP (not advertised) |
| Dismiss | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss` | Implemented |
| OGC API Common Core | `http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core` | Implemented |
| OGC API Common JSON | `http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json` | Implemented |

## Endpoint Coverage

| Capability | Method | Path | Status | Notes |
|---|---|---|---|---|
| Landing page | GET | `/ogc/processes` | Implemented | HATEOAS links to API definition (service-desc → `/ogc/processes/openapi.json`), conformance, processes, jobs |
| OpenAPI spec | GET | `/ogc/processes/openapi.json` | Implemented | Dedicated OpenAPI 3.0.3 document describing OGC Processes endpoints |
| Conformance | GET | `/ogc/processes/conformance` | Implemented | Declares conformance classes listed above |
| Process list | GET | `/ogc/processes/processes` | Implemented | V1: single canonical process (`honua-geoprocessing`) |
| Process description | GET | `/ogc/processes/processes/{processId}` | Implemented | JSON Schema input/output descriptions |
| Execute process | POST | `/ogc/processes/processes/{processId}/execution` | Implemented | Async-only; requires `Prefer: respond-async` and accepts only `response=document`. Successful submissions return `201 Created` with `Location` and `Preference-Applied: respond-async`. Validates plan structure (`planId`, non-empty `steps`, allowed step kinds, string step inputs, string `dependsOn` entries, output artifact kinds) and catalog conformance for geoprocess steps (see [Catalog Validation Semantics](#catalog-validation-semantics)). Returns `503` when Redis-backed durable storage is not configured. Authorization and approval gates match the canonical geoprocessing service. |
| Job list | GET | `/ogc/processes/jobs` | MVP | Returns active jobs only. Supports `limit` query param (must be positive; defaults to `OgcProcesses:DefaultJobLimit`). `conf/job-list` is not advertised because V1 does not support required filters (`type`, `processID`, `status`, `datetime`, `minDuration`, `maxDuration`), `next` pagination, or terminal job enumeration. |
| Job status | GET | `/ogc/processes/jobs/{jobId}` | Implemented | OGC StatusInfo document. V1 intentionally omits the OGC results relation because `/results` is still stubbed. |
| Job results | GET | `/ogc/processes/jobs/{jobId}/results` | Stub | Non-terminal jobs return `404` (result not ready). Failed jobs return `500`. Dismissed jobs return `410 Gone`. Successful jobs return `404` (result storage pending execution engine integration). |
| Dismiss job | DELETE | `/ogc/processes/jobs/{jobId}` | Implemented | Attempts cancellation via `IJobCancellationNotifier`; for remote backends that advertise `SupportsCancellation`, delegates to `IBatchComputeBackend.CancelAsync`; for local backends uses `ExecutionJobCancellationHelper`; already-dismissed jobs return `200`, succeeded/failed jobs return `409 Conflict` |

## Job Status Mapping

The adapter maps canonical `ExecutionJobStatus` values to OGC status strings:

| Canonical status | OGC status |
|---|---|
| Queued | `accepted` |
| Provisioning | `accepted` |
| Running | `running` |
| Succeeded | `successful` |
| Failed | `failed` |
| Cancelled | `dismissed` |

## Catalog Validation Semantics

Geoprocess steps are validated against the built-in `IProcessCatalog` (19 seeded processes across `geometry.*`, `analytics.*`, `generalization.*`, and `data-management.*`) before a job is created. The adapter surfaces the following structured violation codes:

| Code | Meaning |
|---|---|
| `MISSING_PROCESS_ID` | Geoprocess step omitted `processId` |
| `UNKNOWN_PROCESS` | `processId` is not in the built-in catalog |
| `MISSING_REQUIRED_PARAMETER` | A required parameter (or conditionally-required parameter) was not supplied |
| `UNKNOWN_PARAMETER` | Step supplied a parameter name not declared on the process |
| `INVALID_PARAMETER_VALUE` | Value failed type, enum, or range validation |

Typed parameter validation runs against `ProcessParameterValueType` (`Text`, `WholeNumber`, `FloatingPoint`, `Flag`, `Wkb`, `WkbArray`, `Srid`, `LayerId`); WKB inputs must be base64-encoded bytes, `WkbArray` expects a JSON array of base64 strings with at least one element, and `LayerId` requires a non-negative integer (zero-based layer ids are accepted) to match the live `RouteParameterValidator.ValidateLayerId` contract and the handler `int.TryParse` gate (the spatial analytics REST routes constrain `{layerId:int}`). Blank (whitespace-only) values for optional or conditional parameters are treated as "not supplied" to match the handler's `IsNullOrWhiteSpace` gate — so blank conditional inputs surface as `MISSING_REQUIRED_PARAMETER`, not `INVALID_PARAMETER_VALUE`.

Per-process semantic rules mirror the live request handlers so plans accepted here are also accepted at execution time. Upper bounds read from `Limits:Analytics` (`MaxDbscanEpsMeters`, `MaxKMeansK`, `MaxDWithinDistanceMeters`, `MaxBufferDistanceMeters`, `MinDensityCellSizeMeters`, `MaxDensityCellSizeMeters`) are applied here too, so callers see the same rejection boundaries the handlers apply at execution time:

| Process | Enum parameters (allowed values) | Conditional requiredness | Numeric ranges | Cross-field invariants |
|---|---|---|---|---|
| `analytics.cluster` | `algorithm` ∈ {`dbscan`, `kmeans`} | `eps`+`minPoints` when `algorithm=dbscan` (default); `k` when `algorithm=kmeans` | `0 < eps ≤ MaxDbscanEpsMeters`; `minPoints ≥ 1`; `1 ≤ k ≤ MaxKMeansK` | `outStatistics` requires `returnHullPerCluster=true` (per-feature output cannot carry GROUP BY aggregates) |
| `analytics.spatial-join` | `predicate` ∈ {`intersects`, `contains`, `within`, `dwithin`} | `distance` when `predicate=dwithin` | `0 < distance ≤ MaxDWithinDistanceMeters` | `joinLayerId` must differ from `layerId` (no self-join) |
| `analytics.density` | `mode` ∈ {`hex`, `square`} | — | `MinDensityCellSizeMeters ≤ cellSize ≤ MaxDensityCellSizeMeters` | — |
| `analytics.buffer-aggregate` | `unit` ∈ {`meters`, `kilometers`, `feet`, `miles`} | — | `distance ≥ 0`; cap of `MaxBufferDistanceMeters` applied after converting `distance` to meters so alternate units cannot bypass the limit | `outStatistics` requires `dissolve=true` (per-feature buffers cannot carry GROUP BY aggregates) |
| `generalization.simplify-layer` | — | — | `tolerance > 0` (finite); tolerance is expressed in the layer's SRID units (degrees for geographic, meters for projected), matching `geometry.simplify` | — |
| `generalization.dissolve` | same `statisticType` allow-list as `analytics.buffer-aggregate` for `outStatistics` entries | — | — | `outStatistics` requires `dissolve=true` (per-feature output cannot carry aggregate columns) |
| `data-management.copy-features` | — | — | — | `objectIds` parsed as comma-separated integers when supplied |
| `data-management.delete-features` | — | — | — | at least one of `where` / `objectIds` must be supplied (`INVALID_PARAMETER_VALUE`) so deletion is never unbounded |
| `data-management.calculate-field` | — | — | — | `fieldName` must be a simple unquoted identifier (letters, digits, underscore; first char letter or underscore); `expression` is re-gated at execution time by `FeatureServer.Edits.CalculateFieldValue`'s allow-list |

Structured Text-typed inputs that the handlers parse without runtime services are validated here too: `outStatistics` is parsed as JSON (single object or array) and each entry must carry `statisticType` ∈ {`count`, `sum`, `min`, `max`, `avg`, `stddev`, `var`}, `onStatisticField`, and `outStatisticFieldName`, each as a JSON string (numeric, boolean, or object tokens surface as `INVALID_PARAMETER_VALUE` rather than escaping as 500s); `objectIds` must be comma-separated integer feature identifiers; and `spatialRel` rejects the distance-based variants (`esriSpatialRelWithinDistance`, `esriSpatialRelBeyondDistance`) only when a `geometry` filter is also supplied, matching `AnalyticsFeatureQueryFactory` (which does not consult `spatialRel` without geometry) so the validator does not block plans the handler would accept.

The remaining Text-typed inputs on the shared analytics filter bundle (`where`, `geometry`, `geometryType`, `inSR`, `time`, `timeRelation`) are accepted at this gate as opaque strings and validated at execution time by the handler-side filter pipeline (`AnalyticsFeatureQueryFactory`) because they require runtime services (filter expression service, geometry parser, SRID resolver, layer field metadata) not available at catalog-validation time.

## Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `OgcProcesses:DefaultJobLimit` | int | 100 | Maximum jobs returned per list request |

Workspace and retention configuration is shared with the canonical geoprocessing runtime under `Geoprocessing:Workspace`. See [Operations Guide](../../operator/operations.md) for workspace lifecycle settings.

## V1 Limitations

- **Async-only**: synchronous execution returns `501 Not Implemented` when the `Prefer: respond-async` header is absent.
- **Single process projection**: the OGC adapter lists one canonical process (`honua-geoprocessing`) even though the internal catalog now enumerates 19 built-in processes across four families (`geometry.*`, `analytics.*`, `generalization.*`, `data-management.*`). Per-process projection into `/processes` and `/processes/{id}` is follow-on adapter work; executions dispatch through the canonical process and are validated against the built-in catalog at the adapter boundary.
- **Destructive data-management plans require approval**: submission and execution of plans that reference `data-management.delete-features` or `data-management.calculate-field` pass through `OperatorApprovalGate` with `IsDestructive = true`. When `Operator:Approval:DestructiveActionsRequireApproval` is on, these calls hard-fail at the gate before any job or progress record is created — gRPC `FailedPrecondition`, OGC `403` with problem type `about:blank` and title "Approval required" — rather than persisting an `AwaitingApproval` progress entry (pending-approval persistence is follow-on work). Non-destructive `data-management.copy-features` does not set the flag because it materializes a new target layer.
- **Results endpoint**: V1 still stubs the `/results` endpoint — successful jobs return `404` until the execution engine populates result storage. When implemented, the planned V1 shape is a document-mode, by-value JSON response keyed by stable output identifiers. By-reference transmission remains deferred.
- **Planned result document shape**: once result storage is populated, successful `/results` responses will contain outputs only. Job status, summary, and error state remain on job/status endpoints rather than inside `/results`.
- **No results link in StatusInfo**: V1 StatusInfo documents do not include the `http://www.opengis.net/def/rel/ogc/1.0/results` relation because the `/results` endpoint is stubbed. The link will be emitted once result storage is populated by the execution engine.
- **Job list (MVP)**: the `limit` parameter is supported (must be positive); additional query filters (`type`, `processID`, `status`, `datetime`, `minDuration`, `maxDuration`), `next` pagination, and terminal job enumeration are follow-on. `conf/job-list` is not advertised.
- **Job store required**: async execution and all job endpoints return `503 Service Unavailable` when Redis-backed durable storage is not configured.
- **Authorization alignment**: all protected routes enforce `IOperatorAuthorizationEvaluator`; execution additionally enforces `IOperatorApprovalEvaluator`, matching the canonical geoprocessing execute gate.

## Telemetry

- Diagnostic activity protocol tag: `OGC-API-Processes`
- Structured logging event IDs: `8100`–`8199` (reserved block)
- Activity operation tags: `GetProcessList`, `GetProcess`, `ExecuteProcess`, `GetJobList`, `GetJobStatus`, `GetJobResults`, `DismissJob`

## Source Specification

- [OGC API — Processes — Part 1: Core (OGC 18-062r2)](https://docs.ogc.org/is/18-062r2/18-062r2.html)

## Validation and References

- [Geoprocess Framework Analysis](../geoprocess-framework-analysis.md) — cross-protocol comparison (GPServer, OGC API Processes, GeoServer WPS)
- [ADR-0029: Geoprocess Canonical Model Mappings](../../contributor/adr/0029-geoprocess-canonical-model-mappings.md) — adapter contract and lifecycle state mapping
- [Geospatial APIs Overview](../STANDARDS_APIS.md)
