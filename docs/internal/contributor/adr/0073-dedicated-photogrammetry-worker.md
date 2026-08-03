# ADR-0073: Dedicated photogrammetry worker and qualified orthomosaic engine

## Status

Accepted (2026-08). This decision defines the engine and runtime boundary for
RAST-022 (honua-server#3105). It does not declare an orthomosaic process
production-ready; the contract, executor, publication, and evidence slices
remain separately gated.

## Context

Orthomosaic production is not a bounded raster read or a single GDAL utility.
It is a multi-stage photogrammetry workflow that can include feature matching,
camera calibration, bundle adjustment, dense reconstruction, surface
generation, orthorectification, seamline selection, blending, and final raster
encoding. Its CPU, GPU, memory, scratch, and runtime requirements can grow
non-linearly with image count, resolution, overlap, and scene complexity.

ADR-0065 keeps automatic feature matching and general reconstruction out of the
ImageServer process. ADR-0071 classifies orthomosaic production as durable
native/photogrammetric work whose final handoff is a canonical raster artifact.
Neither decision selects a toolchain or says how Honua may execute one without
putting a large native dependency set into the serving image or the general GP
worker.

The initial toolchain comparison considered:

- OpenDroneMap (ODM), an end-to-end command-line and Docker-oriented aerial
  imagery pipeline that produces georeferenced orthorectified imagery and DEMs,
  supports COG output, and documents split/merge processing for large datasets;
- MicMac, a general photogrammetric suite that can produce ortho-imagery; and
- COLMAP, a general Structure-from-Motion and Multi-View Stereo pipeline whose
  documented primary outputs are camera/reconstruction models, depth and normal
  maps, point clouds, and meshes rather than a turnkey georeferenced
  orthomosaic.

ODM is the closest functional reference, but it is AGPLv3 with no alternative
license offered by the project. Shipping or operating it can create
distribution, source-disclosure, notice, modification, and network-use
obligations that Honua must not decide implicitly in a worker Dockerfile.

## Decision

### 1. Product and capability boundary

Orthomosaic production is a versioned canonical raster GP capability executed
only as a durable job. It is never a synchronous ImageServer, OGC, GPServer,
MCP, gRPC, or other request-thread operation. Protocol surfaces may authorize,
validate, plan, submit, monitor, cancel, and resolve results through the shared
process/job runtime; they do not execute photogrammetry.

The first public product is one validated, georeferenced orthomosaic raster.
Optional DEM/DSM products may be added as separately declared logical outputs
after their semantics and publication rules are qualified. Point clouds,
meshes, camera models, depth maps, and other reconstruction products are private
attempt artifacts in this capability. They are not registered or served as
raster products, and their public storage and serving remain the responsibility
of the distinct 3D/point-cloud capability family tracked by #2442.

### 2. Dedicated optional runtime

Photogrammetry runs in a dedicated optional `photogrammetry-worker` runtime
profile:

- **AWS Batch is the primary placement.** It isolates bursty CPU/GPU, memory,
  scratch, and long-duration work from the serving fleet and database.
- **A local container placement is optional.** It supports on-premises,
  air-gapped, and development deployments only when operators provision a
  separately admitted photogrammetry pool with equivalent contracts and hard
  resource limits.
- **The AOT web image never contains or loads the engine, GDAL, CV libraries,
  bindings, CLI tools, or their transitive native packages.** It handles only
  metadata and typed references.
- **The general native GP/GDAL worker never hosts photogrammetry.** That worker
  remains the smaller GDAL utility runtime defined by ADR-0057 and ADR-0071.
  Photogrammetry's larger dependency, license, security, and resource surface is
  independently built, scanned, admitted, upgraded, and disabled.
- **PostGIS is not a reconstruction engine.** It may receive a validated result
  through the normal raster ingest/registration path after the job completes.
  Later serving and analysis then follow the PostGIS-first, database-SLO-aware
  rules in ADR-0071.

This is a trusted built-in capability profile, not ADR-0063 `custom-code`.
Operators cannot change the entrypoint, inject code, or pass arbitrary engine
arguments, and supplying an image digest does not make that digest qualified.
Any capability that accepts an arbitrary operator image, entrypoint, plugin, or
code payload is `custom-code`, remains AWS-Batch-only, and is rejected by the
local photogrammetry profile.

No public ingress is exposed by the worker. The coordinator dispatches an
immutable job manifest through the canonical durable backend. Worker network
access is restricted to approved artifact storage, secrets resolution,
telemetry, and the explicitly configured control plane.

### 3. Provider-neutral adapter and engine binding

The durable executor targets a provider-neutral photogrammetry adapter contract,
not ODM commands. The contract accepts a normalized request, resolves immutable
artifact references into an attempt workspace, invokes one qualified engine,
and returns normalized output and evidence manifests. Engine-specific flags,
filesystem paths, exit codes, progress formats, and intermediate layouts do not
enter the canonical process or protocol contracts.

Every executable attempt is bound before dispatch to:

- engine and adapter identifiers;
- engine and adapter semantic versions;
- an OCI image reference using an exact digest (`repository@sha256:...`), never
  a mutable tag;
- the qualification record and supported semantic variant;
- CPU/GPU placement and resource profile; and
- immutable input and output-set manifest identities.

The binding is stored in the append-only attempt routing record. A retry remains
on the same engine, adapter, semantic variant, and image digest. Changing an
engine or digest is a new planned attempt and is permitted only before mutation,
with explicit semantic-compatibility evidence and policy approval. There is no
silent ODM-to-MicMac fallback.

### 4. ODM is the reference qualification engine, not a distributed default

ODM is selected as the initial **reference and qualification engine** because
its documented workflow covers the intended orthomosaic product, georeferencing
and elevation products, container execution, COG output, camera/GCP inputs, CPU
and GPU choices, and large-dataset split/merge behavior.

Honua does not redistribute, mirror, embed, derive, or set a default ODM image
in the server, web image, native GP image, Helm chart, marketplace artifact, or
other Honua distribution under this decision. An operator that enables the
experimental adapter must provide an allowlisted, immutable ODM image digest
and accept the deployment's license policy. A tag such as `latest` or `3.6` is
invalid even if it currently resolves to a qualified image.

Operator supply is a distribution boundary, not a claim that AGPL obligations
disappear. Before Honua redistributes an ODM binary or image, hosts a modified
ODM service, or presents ODM as an included production backend, an explicit
legal/compliance review must approve at least:

- the exact source, binary, container, modifications, and dependency licenses;
- required copyright and license notices;
- corresponding-source and network-interaction source availability;
- the source/build publication and retention mechanism;
- upgrade, vulnerability, and downstream redistribution policy; and
- marketplace, managed-service, support, and customer documentation impact.

Without both an operator-supplied digest and an accepted qualification record,
the ODM adapter reports unavailable and the canonical capability fails closed
with an actionable configuration result. Honua must not advertise it as
available merely because an arbitrary image can be pulled.

### 5. Alternative engine posture

MicMac is the fallback qualification candidate. Its official project describes
it as a CeCILL-B-licensed photogrammetric suite capable of producing
ortho-imagery. It is not an automatic fallback and receives no implied support
claim. It must pass the same artifact contract, numerical fixtures, container
hardening, license review, resource characterization, and publication gates as
ODM before a MicMac adapter becomes selectable.

COLMAP is not the initial orthomosaic adapter. Its documented SfM/MVS pipeline
is valuable for camera reconstruction, dense point clouds, and meshes, but it
does not provide the complete georeferenced orthomosaic product contract Honua
needs. A future workflow may use COLMAP behind a larger qualified adapter, but
Honua would then own and qualify the missing georeferencing, surface,
orthorectification, seamline, blending, and raster-publication stages. COLMAP's
BSD license alone is not sufficient reason to create that new pipeline now.

### 6. Reference-only input and output contract

Raster or image bytes never pass through the web process, a protocol payload,
Redis, a queue attribute, or an environment variable. The durable job stores
only deliberately small metadata and typed references.

The versioned input manifest references:

- an immutable, ordered image set with media types, encoded sizes, dimensions
  when known, checksums, and stable object versions;
- camera identity, EXIF/orientation data, and optional calibration references;
- optional GCP/control references with CRS, accuracy, and checksum metadata;
- an optional immutable elevation/surface reference;
- target CRS, resolution, extent/boundary, NoData/alpha policy, quality profile,
  and explicitly allowlisted process parameters; and
- scoped credential references, never raw credentials or presigned URLs whose
  lifetime is shorter than the admitted job.

Arbitrary local paths, shell fragments, engine flags, unbounded remote URLs, and
inline image collections are invalid. Executors resolve authorized references
directly to an attempt-scoped workspace and verify versions, sizes, and
checksums before engine invocation.

The declared output is an attempt-scoped immutable COG or GeoTIFF reference plus
checksum, media type, byte size, CRS, affine grid, dimensions, band semantics,
NoData/alpha, overview/statistics evidence, lineage, and engine quality report.
Only the coordinator may promote that private output through the canonical
raster output-set and ingest protocol.

### 7. Admission and placement

Admission occurs before staging or engine execution. The planner estimates and
records at least image count, encoded bytes, decoded pixels and bands, expected
overlap/feature workload, requested quality, expected output pixels, CPU/GPU,
memory, scratch, I/O, and duration. Unknown dimensions, unsupported camera or
calibration combinations, an unbounded output grid, an unavailable qualified
backend, or an estimate above a hard budget fails closed before allocation.

Photogrammetry has independent global and per-tenant concurrency, queued-work,
CPU/GPU, memory, scratch, runtime, and artifact-byte budgets. It does not borrow
the web-output budget, general GP worker admission, or database-work budget.
Operator policy may cap or deny profiles but cannot bypass capability,
authorization, license, image-digest, security, semantic, or resource gates.

AWS Batch is preferred for every production attempt until benchmark evidence
defines a bounded local envelope. Local execution is never selected merely
because Batch is temporarily unhealthy; fail, defer, or replan before execution
according to explicit policy.

### 8. Durable lifecycle, cancellation, and cleanup

The canonical job contract supplies durable attempt identity, idempotency,
claim/fencing tokens, heartbeats, deadlines, and cancellation. Each attempt owns
one workspace and one immutable output prefix. Duplicate delivery reuses the
same attempt identity and cannot create a second published output.

Cancellation prevents new phases, propagates to the container, allows a bounded
grace period for cooperative shutdown, and then terminates the local container
or Batch job. Cancellation, timeout, and failure leave all outputs private.
Cleanup removes or quarantines attempt-scoped scratch and partial artifacts by
retention policy without deleting a previously published raster. Engine resume
or checkpoint support is disabled until a qualified adapter proves that it is
attempt-fenced and deterministic for the same inputs and digest.

Retries are allowed only for classified transient failures and immutable inputs.
An engine process exit, an invalid product, a resource-budget breach, or a
quality-gate failure is not converted silently into success or another engine.

### 9. Validation and atomic raster handoff

Engine exit success is not job success. Before publication, an isolated
validation step verifies the complete expected output set, checksums, readable
GeoTIFF structure, COG requirements when COG is requested, CRS and grid,
dimensions, bands, NoData/alpha, bounds, overviews/statistics, and configured
quality thresholds. It also proves that output paths stay inside the attempt
prefix and that no unexpected artifact is selected for publication.

Only validated artifacts enter the ordinary raster ingest and catalog-readiness
workflow. Optional PostGIS materialization is a downstream raster placement,
not part of reconstruction. Publication uses the attempt-fenced output-set
protocol in ADR-0071: the raster remains invisible until every required artifact
and registration is complete. Failure or cancellation never exposes a partial
mosaic, half-written stable key, or catalog row without a complete manifest.

### 10. Security and supply-chain gate

Each qualified image/digest requires:

- a software bill of materials, vulnerability scan, signature/provenance check,
  and documented upstream source/build identity;
- an allowlisted registry/repository and deployment-specific license approval;
- non-root execution, a read-only root filesystem, bounded writable scratch,
  dropped capabilities, and no host or Docker socket access, unless an exception
  is explicitly reviewed and recorded;
- least-privilege read access to declared inputs and write access only to the
  attempt prefix, with short-lived credentials resolved at execution;
- constrained egress and no user-controlled process arguments;
- preflight limits for file count, dimensions, encoded/decoded size, archive
  expansion, path traversal, and malformed image/metadata inputs; and
- sanitized logs and reports that contain no credentials, source bytes,
  sensitive EXIF values, or arbitrary high-cardinality labels.

A digest change invalidates qualification. Patch upgrades therefore produce a
new evidence record and controlled rollout instead of following a mutable tag.

### 11. Lineage, observability, and qualification evidence

The published raster lineage records source and control/calibration checksums,
optional surface identity, canonical request and parameter hashes, engine and
adapter versions, image digest, placement, target grid, and output checksum. The
attempt record also captures estimates and actual CPU/GPU, peak memory, peak
scratch, I/O, duration, exit classification, cancellation latency, and per-phase
timings. Logs and quality reports are artifact references with bounded
retention; metrics use bounded dimensions.

An engine/digest/semantic variant is production-qualified only after evidence
covers:

1. independently sourced fixtures for georeferencing, GCPs, camera/lens models,
   CRS/grid/NoData, seamlines, radiometry, and COG/GeoTIFF validation with stated
   numerical tolerances;
2. repeatability tests for the same image digest, input manifest, parameters,
   and CPU/GPU profile, with known non-determinism documented;
3. local and object-store/AWS Batch integration, including large inputs without
   bytes entering the web heap or Redis;
4. admission refusal, duplicate delivery, retry, cancellation, timeout, stale
   attempt, partial-output, and cleanup tests;
5. measured resource envelopes and actual-versus-estimate telemetry; and
6. license, SBOM, signature/provenance, vulnerability, secrets, sandbox, and
   egress evidence.

Capability discovery reports the engine unavailable until all mandatory gates
for the configured digest and placement are current.

## Consequences

### Positive

- The native-AOT web image and general GP worker remain small and free of the
  photogrammetry dependency and license surface.
- AWS Batch absorbs bursty reconstruction load without turning PostGIS into a
  photogrammetry compute cluster or threatening database SLOs.
- ODM provides a realistic reference without committing Honua to redistribution
  before AGPL compliance is explicitly approved.
- A provider-neutral contract preserves an engine replacement path and prevents
  protocol or SDK drift.
- Typed references, fenced staging, and ordinary raster ingestion give the
  orthomosaic the same security and publication guarantees as other raster GP
  outputs.

### Negative

- No zero-configuration orthomosaic backend ships in the main distribution.
- Operators enabling the reference adapter must supply a qualified digest and
  own deployment/license configuration.
- Honua must maintain engine-specific adapters, qualification fixtures, resource
  evidence, and upgrade records for every supported digest.
- Dedicated local and Batch profiles add deployment and operational complexity.

## Alternatives considered

- **Bundle ODM in the web or main distribution.** Rejected because it violates
  the AOT/native dependency boundary and bypasses an explicit AGPL compliance
  decision.
- **Add ODM to the general GDAL worker.** Rejected because reconstruction has a
  larger and different dependency, resource, security, and license surface than
  bounded GDAL utilities.
- **Use PostGIS for reconstruction.** Rejected because PostGIS Raster can store,
  validate, serve, and analyze the result but is not the required
  photogrammetric reconstruction engine.
- **Select MicMac solely for its license.** Rejected for the initial reference
  because ODM more directly matches the intended end-to-end product and
  container workflow; MicMac remains a qualification candidate.
- **Build the first adapter from COLMAP components.** Rejected because Honua
  would own substantial missing orthomosaic stages before delivering the raster
  product.
- **Automatically switch engines after failure.** Rejected because engine
  outputs and failure boundaries are not assumed semantically identical.

## Implementation sequence

This ADR completes only RAST-022 slice A. The remaining ordered slices are:

1. **Contract and planner gate:** versioned manifests, capability registration,
   validation, estimates, independent admission, and fail-closed availability.
2. **Isolated executor:** provider-neutral adapter plus one operator-supplied ODM
   digest exercised through local and AWS Batch placements with durable
   lifecycle and cancellation.
3. **Validation and publication:** COG/GeoTIFF validation, immutable promotion,
   optional PostGIS materialization, and canonical raster catalog registration.
4. **Evidence and operability:** fixtures, cloud and failure-path integration,
   lineage, estimate/actual telemetry, dashboards, alerts, retention, cleanup,
   and runbooks.

No implementation slice may weaken this ADR's dependency, license, reference,
admission, security, or publication gates.

## References

- [ADR-0031: Durable Job Orchestration Substrate](0031-durable-job-orchestration-substrate.md)
- [ADR-0057: Geoprocessing Capability Boundaries](0057-geoprocessing-capability-boundaries.md)
- [ADR-0063: Custom-Code Execution Is AWS-Batch-only](0063-custom-code-execution-is-aws-batch-only.md)
- [ADR-0065: ImageServer Photogrammetric Analytics](0065-imageserver-photogrammetric-analytics.md)
- [ADR-0071: Raster Execution Boundary](0071-raster-execution-boundary.md)
- [OpenDroneMap documentation](https://docs.opendronemap.org/)
- [OpenDroneMap large-dataset split/merge](https://docs.opendronemap.org/large/)
- [OpenDroneMap licensing FAQ](https://docs.opendronemap.org/faq/#licensing)
- [OpenDroneMap ODM repository](https://github.com/OpenDroneMap/ODM)
- [MicMac presentation and capability summary](https://micmac.ensg.eu/index.php/Presentation)
- [MicMac repository](https://github.com/micmacIGN/micmac)
- [COLMAP output format](https://colmap.readthedocs.io/en/latest/format.html)
- [COLMAP repository and license](https://github.com/colmap/colmap)
- honua-io/honua-server#3105 (RAST-022)
- honua-io/honua-server#2442 (separate 3D/point-cloud serving)
