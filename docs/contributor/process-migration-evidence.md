# Process Migration Evidence

Last reviewed: 2026-05-18

This page defines the first server-side evidence slice for migrated
geoprocessing workloads. It is intentionally narrower than full ArcPy,
ModelBuilder, GeoServer WPS, or arbitrary OGC API Processes portability.

## Claim Scope

Honua can project a first set of deterministic vector processes as concrete
process ids through OGC API Processes and as GPServer tasks, submit async jobs
through the canonical process runtime, persist terminal result packages, and
expose result artifact references through:

- OGC API Processes: `GET /ogc/processes/jobs/{jobId}/results`
- GeoServices GPServer:
  `GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}`

The current scaffold does not claim full source-script translation or full
runtime parity for every built-in process. Python SDK ArcPy scanning and parity
execution remains tracked in
[honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59).

## First Supported Vector Set

| Process family | Process ids | Evidence posture |
|---|---|---|
| Geometry vector operations | `geometry.buffer`, `geometry.clip`, `geometry.intersect`, `geometry.project`, `geometry.simplify`, `geometry.make-valid`, `geometry.union`, `geometry.difference`, `geometry.area`, `geometry.length` | Automated first-slice projection. OGC API Processes lists concrete ids and accepts direct per-process async execution requests. GPServer exposes the same ids as tasks. |
| Simple analysis and layer vector operations | `analytics.buffer-aggregate`, `analytics.spatial-join`, `analytics.density`, `analytics.cluster`, `conversion.feature-project`, `generalization.simplify-layer`, `generalization.dissolve` | Automated first-slice projection where canonical validation can produce deterministic vector/table output expectations. |

## Classification Contract

| Classification | Families | Behavior |
|---|---|---|
| Automated | First supported vector set above | Projected as concrete OGC API Processes ids and GPServer tasks. Async submission produces a canonical job; terminal result packages are adapted into protocol result routes when the executor publishes artifact references. |
| Assisted | `surface.*`, `raster.*`, raster conversions, non-destructive data-management inventory | Catalog/validation and migration inventory only for this evidence slice. Do not present as automated runtime parity. |
| Manual review | `data-management.delete-features`, `data-management.calculate-field` | Destructive. Submission routes through the operator approval gate before a job/progress record is created. |
| Unsupported | Any process/source family outside the explicit set | No migration portability claim. WPS/OGC source-process import is out of scope unless a later fixture and evidence artifact adds it. |

## Result Evidence Contract

Successful jobs must not count as process migration proof unless they expose
non-empty result artifacts. The protocol contracts are:

- OGC API Processes returns a document-mode JSON object keyed by stable output
  parameter names such as `outputFeatureLayer`.
- GPServer returns per-output responses under `results/{paramName}`, with
  `paramName`, Esri GP data type, and an artifact value/reference.
- Artifact metadata should carry
  `geoservices.output_parameter` when a stable GPServer output binding is known.
- Failed and cancelled jobs remain status evidence, not successful migration
  result evidence.

## Fixture Scaffold

The committed fixtures under `tests/fixtures/process-migration/` define the
shape of the first parity lane:

- `vector-process-parity-fixture.json` lists deterministic process cases and
  expected schema/geometry/count/metadata comparison points.
- `expected-evidence-artifact.json` defines the result-route and evidence
  envelope checks expected from a server or SDK runner.

These fixtures are scaffold contracts. A later evidence run must populate real
execution results before broader process portability wording is used.

## Targeted Verification

```bash
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~ProcessMigrationEvidence|FullyQualifiedName~OgcProcessesEndpointsTests|FullyQualifiedName~OgcProcessesJobResultsTests|FullyQualifiedName~GPServerDurableRuntimeTests"
```
