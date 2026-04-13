# OGC API Processes Coverage (V1)

This page summarizes Honua V1 support for OGC API Processes Part 1 — Core.

Honua implements OGC API Processes as a **protocol adapter** over the canonical geoprocessing runtime. The adapter translates between OGC API Processes conventions and Honua's internal process model without adding protocol-specific domain types. See [ADR-0029](../../contributor/adr/0029-geoprocess-canonical-model-mappings.md) for the canonical model mapping and [Geoprocess Framework Analysis](../geoprocess-framework-analysis.md) for the cross-protocol comparison.

## Conformance Classes

| Conformance class | URI | Status |
|---|---|---|
| Core | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core` | Implemented |
| JSON | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json` | Implemented |
| Job List | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list` | Implemented |
| Dismiss | `http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss` | Implemented |
| OGC API Common Core | `http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core` | Implemented |
| OGC API Common JSON | `http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json` | Implemented |

## Endpoint Coverage

| Capability | Method | Path | Status | Notes |
|---|---|---|---|---|
| Landing page | GET | `/ogc/processes` | Implemented | HATEOAS links to conformance, processes, jobs |
| Conformance | GET | `/ogc/processes/conformance` | Implemented | Declares conformance classes listed above |
| Process list | GET | `/ogc/processes/processes` | Implemented | V1: single canonical process (`honua-geoprocessing`) |
| Process description | GET | `/ogc/processes/processes/{processId}` | Implemented | JSON Schema input/output descriptions |
| Execute process | POST | `/ogc/processes/processes/{processId}/execution` | Implemented | Async-only; requires `Prefer: respond-async` header |
| Job list | GET | `/ogc/processes/jobs` | Implemented | Paginated; limit controlled by `OgcProcesses:DefaultJobLimit` |
| Job status | GET | `/ogc/processes/jobs/{jobId}` | Implemented | OGC StatusInfo document |
| Job results | GET | `/ogc/processes/jobs/{jobId}/results` | Implemented | Document-mode, by-value JSON |
| Dismiss job | DELETE | `/ogc/processes/jobs/{jobId}` | Implemented | Cancels running jobs via `IJobCancellationNotifier` |

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

## Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `OgcProcesses:DefaultJobLimit` | int | 100 | Maximum jobs returned per list request |

Workspace and retention configuration is shared with the canonical geoprocessing runtime under `Geoprocessing:Workspace`. See [Operations Guide](../../operator/operations.md) for workspace lifecycle settings.

## V1 Limitations

- **Async-only**: synchronous execution returns `501 Not Implemented` when the `Prefer: respond-async` header is absent.
- **Single process**: the process catalog exposes one canonical process (`honua-geoprocessing`). Catalog formalization is follow-on work.
- **Document-mode results only**: results are returned by value as a JSON document. By-reference transmission is not supported in V1.
- **Result content**: the results document structure will evolve as the execution engine matures.

## Telemetry

- Diagnostic activity protocol tag: `OGC-API-Processes`
- Structured logging event IDs: `8100`–`8159`
- Activity operation tags: `GetProcessList`, `GetProcess`, `ExecuteProcess`, `GetJobList`, `GetJobStatus`, `GetJobResults`, `DismissJob`

## Source Specification

- [OGC API — Processes — Part 1: Core (OGC 18-062r2)](https://docs.ogc.org/is/18-062r2/18-062r2.html)

## Validation and References

- [Geoprocess Framework Analysis](../geoprocess-framework-analysis.md) — cross-protocol comparison (GPServer, OGC API Processes, GeoServer WPS)
- [ADR-0029: Geoprocess Canonical Model Mappings](../../contributor/adr/0029-geoprocess-canonical-model-mappings.md) — adapter contract and lifecycle state mapping
- [Geospatial APIs Overview](../STANDARDS_APIS.md)
