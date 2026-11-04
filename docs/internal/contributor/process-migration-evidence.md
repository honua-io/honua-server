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

## Toolbox Translation Lane (#2145)

The arcpy/toolbox (`.pyt`/`.tbx`/`.atbx`) translation lane is split across
repos by design:

- **honua-sdk-python** (`honua-migrate`, sdk issues #59/#123/#124) owns
  parsing toolbox sources, scanning arcpy constructs, and proposing per-tool
  mappings onto native Honua processes. Binary `.tbx`/`.atbx` parsing remains
  an explicit `UnsupportedToolboxError` stub SDK-side.
- **honua-server** owns the round-trip proof and the canonical runtime. The
  process catalog is the single source of truth for executable signatures, so
  the server validates SDK-translated manifests via
  `POST /api/v1/admin/import/toolbox/translation/validate`
  (`ToolboxTranslationEndpoints` -> `ToolboxTranslationValidator`) and returns
  a `honua.migration.toolbox-translation-report` artifact: per-tool
  classification (`translated` / `partially-translated` / `unsupported`),
  round-tripped canonical parameter bindings, and explicit issue codes
  (`no-native-executor`, `unknown-process`, `unknown-target-parameter`,
  `duplicate-target-parameter`, `missing-required-parameter`,
  `unsupported-construct`, `unsatisfied-conditional-inputs`,
  `unverifiable-conditional-branches`, `process-not-job-executable`). Tools that cannot
  supply a required parameter are `unsupported`, never stubbed as executable.
  Inbound manifests must carry a supported `artifactKind`/`artifactVersion`;
  an incompatible identity is rejected rather than reinterpreted.

Static `Required` flags are not the whole admissibility contract: several
processes declare mutually-substitutable optional inputs (for example the
raster `source`/`layerId`/`rasterId` trio) that only the canonical plan
validator enforces at submit time. Rather than re-implement those rules, the
lane asks the canonical validator itself through
`IProcessConditionalInputProbe` (Core abstraction, implemented in
`Honua.Geoprocessing` by `ProcessConditionalInputProbe` over
`ProcessPlanValidator`). A mapping the submit path would reject is reported
`unsatisfied-conditional-inputs` and classified `unsupported`, so the report
never certifies a tool that submit-time validation will refuse. The probe
answers strictly from parameter presence and filters to
`MISSING_REQUIRED_PARAMETER` failures, because callers supply parameter names
rather than real values. The probe runs the direct-submit guards alongside
`ProcessPlanValidator`, so a target that is not job-dispatchable at all (the
sync-only `analytics.cluster`/`analytics.density` ids, which run only through
the synchronous layer-scoped analytics surface) is reported
`process-not-job-executable` and classified `unsupported` whatever its
parameter mapping looks like — translated tools execute through the canonical
job runtime, so callers are pointed at the `-managed` counterparts.

The catalog models conditional requirements nowhere: `ProcessParameterSpec`
carries only `Required`, `DefaultValue` and `AllowedValues`, so requirements
such as "`k` is required when `algorithm=kmeans`" exist solely as per-process
rules inside `ProcessPlanValidator`. The lane therefore asks that validator
which unmapped parameters it can actually require, instead of assuming every
optional omission is branch-dependent. An omission the validator requires on
no branch is unconditionally optional and admissible at submit time — the
submit path accepts `geometry.dissolve` without `groupKeys` — so those
mappings stay `translated`. Downgrading them would report most executable
mappings as needing review.

The signal is deliberately conservative in the other direction, because
claiming a tool executable when a branch is genuinely unproven is the worse
error. `unverifiable-conditional-branches` (classified
`partially-translated`: reviewable, never certified executable) still fires
for an unmapped, defaultless parameter when either

- some enumerable assignment of the mapped parameters makes the canonical
  validator require it (`transform.dedup` requires `keys` whenever the mapped
  `geometry` flag is false), or
- a mapped parameter's legal values cannot be enumerated at all, because the
  validator constrains it to a token set the catalog does not declare — only
  three parameters repo-wide declare `allowedValues`, so discriminators like
  `analytics.cluster-managed`'s `algorithm` and `transform.computed-field`'s
  `op` fall here and every candidate stays reported.

The second case is detected by asking the validator whether it rejects several
structurally different legal-looking values for the parameter (an identifier,
a GUID, a numeric list); a token domain rejects all of them, while a
format-only rule such as `connectionId`'s GUID check accepts one. Declaring
`allowedValues` for a discriminator moves it out of this bucket and into exact
branch enumeration, which is the follow-up path for tightening the report.

The server never parses toolbox sources and never emulates arcpy execution;
translated tools execute only as existing native processes through the
canonical process/job runtime (OGC API Processes / GPServer).

Contract fixtures live under `tests/fixtures/toolbox-translation/`
(translatable, partially translatable, and fully unsupported toolboxes) and
are exercised end-to-end by `ToolboxTranslationEndpointTests` plus the
classification-rule unit tests in `ToolboxTranslationValidatorTests`.
