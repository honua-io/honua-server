# Point Cloud, Drone, and Reality-Capture Ingest

This page is a research-spike output for `honua-server-900`. It maps candidate
drone, point-cloud, and reality-capture formats to Honua's existing scene,
storage, upload, and job infrastructure, recommends a first ingest path that
runs locally without cloud spend, and lists bounded follow-up implementation
issues.

**No production point-cloud ingest pipeline, conversion executor, COPC
streaming adapter, GPU/Batch executor, or change-detection job is added by
this spike.** Honua's current 3D demo path remains hosted OGC 3D Tiles served
from `SceneEndpoints` and produced either externally or via the
[3D Tiles generation pipeline](scene-generation.md) for PostGIS feature
layers.

## Recommendation

The first ingest path is **pre-tiled 3D Tiles uploaded to local storage and
registered through the existing scene dataset registry**.

This is the right first slice because:

- The hosted serving path already accepts the canonical 3D Tiles MIME types
  the format produces, including `.b3dm`, `.i3dm`, `.pnts`, `.cmpt`, `.glb`,
  `.gltf`, `.bin`, `.ktx`, `.ktx2`, and `.basis` (see
  [Hosted 3D Tiles Scenes](scenes-3dtiles.md#mime-types)). `.pnts` covers the
  point-cloud variant of 3D Tiles natively.
- The [scene dataset registry](../admin-api/scene-dataset-registry.md) admin
  API (`POST /api/v1/admin/scenes`) registers an `AssetRoot` directory plus a
  root `tilesetFileName` with no new code.
- Operators run the conversion outside Honua using existing open-source
  toolchains: `py3dtiles` for LAS/LAZ → `tileset.json` + `.pnts` 3D Tiles
  (the only mainstream open-source converter that targets the OGC 3D
  Tiles point-cloud profile today), PDAL for upstream reprojection,
  denoising, decimation, and COPC output, and photogrammetry exports
  from Metashape / RealityCapture for mesh tilesets. Zero new server
  dependencies, zero cloud spend, zero new endpoints.
- The
  [NVIDIA construction demo fixture](../demo/nvidia-construction.md) already
  shows the end-to-end shape with placeholder b3dm tiles and an
  observations sidecar; replacing the placeholders with real drone-derived
  3D Tiles is a content swap, not a code change.

Honua does **not** ingest, convert, or process raw LAS/LAZ/COPC/E57 in this
slice. Those paths are documented as bounded follow-ups below.

## Format decision matrix

| Format | Category | First-demo cost on Honua | Stored as | NVIDIA path | Notes |
| --- | --- | --- | --- | --- | --- |
| **Pre-tiled 3D Tiles** (b3dm, i3dm, pnts, cmpt, glb) | Mesh / point cloud tiles | **Zero** — already a hosted MIME family in `SceneEndpoints` | Hosted directory + scene-registry record | cuSpatial post-process, Omniverse/USDA manifest export | Operator pre-tiles externally; covers both mesh and point-cloud variants. |
| **GLB / glTF** | Single mesh asset | Zero — already a hosted MIME (`model/gltf-binary`, `model/gltf+json`) | Hosted asset under a tileset, or a future single-asset scene type | Same as pre-tiled | Common photogrammetry export from Metashape or RealityCapture. |
| **COPC** (Cloud-Optimized Point Cloud) | Point cloud | Low — HTTP-range read of a single `.copc.laz` file; needs allowed-MIME entry and a thin range-aware route | Source artifact (single file) | CUDA-PDAL filters, cuSpatial | Best streaming format for raw point clouds; LAZ-based octree, no server-side reader required for first slice. |
| **LAS / LAZ** | Point cloud | Medium — needs a CPU conversion job: `py3dtiles` for `.pnts` 3D Tiles output, PDAL for COPC output and upstream prep (reproject, denoise, decimate) | Source artifact + derived tiles | CUDA-accelerated PDAL filters | Industry-standard scanner output; large files; conversion is a follow-up executor. |
| **E57** | Point cloud | High — no native .NET reader; libE57 is C++ | Source artifact only (deferred) | Same as LAS/LAZ once converted | Scanner-vendor format; converters exist (`pdal --reader e57` requires plugin); not recommended for first demo. |
| **OBJ** | Single mesh | Low — trivial offline conversion to GLB | Convert once, store as GLB | Same as GLB | Very large uncompressed; do not ingest natively. |
| **Photogrammetry exports** (Metashape, RealityCapture, OpenDroneMap) | Multi-output | Low when exported as 3D Tiles or GLB | Whichever the export tool produced | Full NVIDIA pipeline at the producer | Treat as a "format upstream of Honua"; operator chooses 3D Tiles or GLB. |
| **Orthoimagery (GeoTIFF / COG)** | Raster | Use the existing [raster pipeline](raster-overview.md), not this spike | COG-direct serve or registered raster | RAPIDS / cuSpatial post-process | Out of scope here; orthoimagery flows through the COG path, not 3D scenes. |

The first-demo recommendation drops to **pre-tiled 3D Tiles** alone. COPC is
listed as the strongest second slice because its octree is already in the
file and requires no server-side LOD intelligence to stream.

## Demo ingest path (local, no cloud spend)

1. Operator captures a site with a drone or terrestrial scanner.
2. Operator runs an external open-source pipeline:
   - **Mesh**: photogrammetry tool (Metashape, RealityCapture, OpenDroneMap)
     exports a 3D Tiles directory or a GLB.
   - **Point cloud**: `py3dtiles` converts a LAS/LAZ source to a
     `tileset.json` plus `.pnts` tile tree (the only mainstream
     open-source converter that targets the OGC 3D Tiles point-cloud
     profile today). PDAL is used for upstream preparation — reading
     LAS/LAZ/E57, reprojecting to ECEF, denoising, decimation — and for
     emitting a COPC artifact when serving the source point cloud is
     preferred over tiling. PotreeConverter is **not** part of the
     hosted-3D-Tiles path: it emits the Potree-native `metadata.json` /
     octree layout, which is not a 3D Tiles tileset and would require a
     separate Potree-serving adapter that is not in scope for this
     spike.
3. Operator copies the tileset directory into the deployment's scene asset
   root (`AssetRoot` on the registry record — see
   [scene dataset registry](../admin-api/scene-dataset-registry.md)).
4. Operator calls `POST /api/v1/admin/scenes` with the slug, asset root, and
   tileset filename. The hosted serving path begins answering immediately
   from `/scenes/{sceneId}/tileset.json`.
5. CesiumJS or another 3D Tiles client loads the URL exactly as it does for
   the [NVIDIA construction demo](../demo/nvidia-construction.md).

No new ingest job, conversion executor, or GPU/Batch resource is touched.
The scene `AssetRoot` is a filesystem directory canonicalized at startup;
the hosted serving routes resolve every tile through the local filesystem
(`Path.Combine` + `FileInfo` in
`src/Honua.Server/Features/Protocols/Scene/SceneAssetResolver.cs`,
served via `Results.File` in `SceneEndpoints.cs`). On S3/Azure
deployments the operator copies or syncs the tileset directory onto the
scene server's mounted volume — there is no `ICloudFileStorage`-backed
asset provider on the read path today. Wiring a cloud-object-backed
scene asset provider, or a sync/promotion step from blob storage onto
the mounted `AssetRoot`, is a separate follow-up and not part of this
spike.

## Capture-session metadata contract (proposed)

Pre-tiled assets cover the *serving* shape but not the *capture* shape —
operators need a place to record when, how, and by what tool a scene was
captured. The proposal is two new entities introduced in a follow-up
ticket; they reference the existing `SceneDataset` rather than replacing it.

### `CaptureSession`

| Field | Type | Notes |
| --- | --- | --- |
| `captureSessionId` | Guid | Stable identifier. |
| `projectId` | string? | Free-form scope key for now; FK upgrade is a separate ticket once a `Project` entity exists. |
| `capturedAt` | DateTimeOffset | UTC. Enables temporal comparison across sessions. |
| `captureMethod` | enum | `Drone`, `Terrestrial`, `Handheld`, `Mobile`, `Synthetic`. |
| `sourceFiles` | string[] | Storage URIs (or registered blob paths) for raw input artifacts. |
| `extent` | object? | WGS-84 axis-aligned bounding box `{ xMin, yMin, xMax, yMax, zMin?, zMax? }`. Same shape the registry already uses. |
| `crs` | string? | Authority token for the capture's source CRS (`EPSG:32610`, `EPSG:4979`); never assumed. |
| `pointCount` | long? | Total points in the source artifact when known. |
| `nominalAccuracyM` | double? | Stated horizontal accuracy in meters. |
| `processingTool` | string? | Tool name and version used for any pre-Honua conversion (`pdal 2.7`, `metashape 2.1`). |
| `status` | enum | `Pending`, `Processing`, `Ready`, `Failed`, `Archived`. |
| `createdAt` / `updatedAt` | audit | UTC. |

### `ProcessedAsset`

| Field | Type | Notes |
| --- | --- | --- |
| `processedAssetId` | Guid | Stable identifier. |
| `captureSessionId` | Guid | FK → `CaptureSession`. |
| `assetType` | enum | `PointCloud3dTiles`, `MeshGlb`, `Copc`, `OrthoGeoTiff`, `Dsm`, `Laz`. |
| `sceneDatasetId` | Guid? | FK → `SceneDataset` when the asset is served via `SceneEndpoints`. Null for source-only artifacts. |
| `storageUri` | string | Path to the asset (directory for tilesets, file for COPC/LAZ). |
| `tilesetRootFile` | string? | Defaults to `tileset.json` when applicable. |
| `generatedAt` | DateTimeOffset | UTC. |
| `toolchainVersion` | string? | Pre-Honua tool version. |
| `status` | enum | `Pending`, `Ready`, `Failed`. |

Temporal comparison ("show the structure tile from `capturedAt = 2026-04-10`
next to `2026-05-04`") is satisfied for Phase 1 by querying
`CaptureSession.capturedAt` across sessions sharing the same `projectId`. A
dedicated time-series store is premature.

## Required server contracts

Any future implementation must define these contracts before writing code:

| Contract | Requirement |
| --- | --- |
| Admin endpoints | Follow the existing `SceneDatasetEndpoints` pattern: `POST /api/v1/admin/capture-sessions`, `GET /api/v1/admin/capture-sessions/{id}`, `PUT`, `GET ?projectId={pid}`, and `POST /api/v1/admin/capture-sessions/{id}/processed-assets`. |
| Domain placement | Entities live in `Honua.Core/Features/Scene/Domain/` (or a sibling `Capture/Domain/`); persistence and repository implementations stay in `Honua.Postgres` as `internal sealed`. `Honua.Core` must not reference `Honua.Server` or provider projects. |
| Storage layout | Raw source uploads (LAS/LAZ/COPC) land under `captures/{sessionId}/source/` via `ICloudFileStorage` and the streaming upload pipeline. Derived 3D Tiles must land on the filesystem `AssetRoot` that the scene dataset record points at, because hosted serving resolves files through `Path.Combine` + `FileInfo` rather than `ICloudFileStorage`. The Phase 2 executor either writes directly to the mounted `AssetRoot` or syncs/promotes the converted tileset from blob storage onto that path. A cloud-object-backed scene asset provider that lets `SceneEndpoints` read directly from `ICloudFileStorage` remains a separate follow-up; this spike does not assume one. |
| Allowed MIME types | `FileUploadSecurity` does not currently allow point-cloud MIME types. Add `application/vnd.las`, `application/vnd.laszip`, `application/vnd.copc` (or the agreed canonical set), with content-sniffing for the `LASF` magic bytes for safety. |
| Backpressure | All raw uploads must flow through `StreamingFileUploadService` (bounded `Channel<T>` with `BoundedChannelFullMode.Wait`); LAS/LAZ/COPC artifacts can be multi-GB and must never buffer in memory. |
| Job kind | A new `ExecutionJobKind.PointCloudIngest` value extends the existing enum (`Geoprocessing`, `ExtractTransformLoad`, `TileCache` today) for the CPU conversion path. A separate `PointCloudIngestGpu` is added when the GPU/Batch slice is implemented. |
| Executor pattern | A new `IJobExecutor` implementation registered in DI, parallel to the placeholder slot the v1 pipeline already exposes. Conversion shells out via `Process.Start` to `py3dtiles` for `.pnts` tileset output, and to PDAL for upstream prep (reprojection, denoising, decimation) and COPC output. Shell-out keeps the server image slim and AOT-safe; no managed point-cloud library is added. |
| Progress reporting | `IUploadProgressStore` for upload progress; `IExecutionLogStore` for executor-side log lines. Both are the same surfaces the existing job pipeline uses. |
| GPU/Batch | `AwsBatchJobSubmission` already carries an optional `GpuCount`; the GPU follow-up wires that through `ExecutionJobSpec` and selects a CUDA-PDAL container image. The Azure path reuses `AzureBatchClient` analogously. |
| Geodesy | CRS must be stored explicitly on `CaptureSession`; never assume EPSG:4326. Vertices in 3D Tiles output must be projected to ECEF (EPSG:4978) per the existing pipeline. Vertical datum is recorded as unknown when not declared. |
| Telemetry | Add observable operations such as `capture.session.register`, `capture.asset.register`, and (in Phase 2) `capture.ingest.pointcloud` with `captureSessionId`, `processedAssetId`, `assetType`, and validation/exception status tags. |
| Validation | Reuse the canonical `SceneDatasetValidator` for any slug/displayName/cache fields surfaced on derived `SceneDataset` records. Range-validate coordinates and bounds at the admin endpoint boundary. |
| Tests | Fast-tier unit tests for the metadata model and validator; integration tests for the admin CRUD using the existing `WebAppFixture` + Postgres testcontainer pattern. The Phase 2 conversion executor needs a fixture-tier integration test with a small synthetic LAS file. |

## NVIDIA relevance (explicit)

| Capability | Tool / runtime | Phase | Honua surface |
| --- | --- | --- | --- |
| Point-cloud tiling and reprojection | `py3dtiles` for `.pnts` tile generation; PDAL (with CUDA filters in Phase 3) for reprojection and source preparation | Phase 2 (CPU first, GPU follow-up) | New `IJobExecutor` + `ExecutionJobKind.PointCloudIngest`. |
| Noise removal, ground classification, segmentation | CUDA-PDAL plugin or PointNet++ on a Batch job | Phase 3 | `ExecutionJobKind.PointCloudIngestGpu`; Batch container image with CUDA. |
| Photogrammetry reconstruction | Metashape GPU, RealityCapture | Pre-Honua (operator toolchain) | None — Honua stores the operator's output. |
| Change detection between capture sessions | cuSpatial point-cloud diff | Phase 3 | New `ExecutionJobKind.ChangeDetection` consuming two `CaptureSessionId` values. |
| Simulation / digital twin handoff | Omniverse Isaac, Omniverse connectors | Future | Connects through the [OpenUSD/Omniverse export path](openusd-omniverse-export-path.md) (`honua-server-901`). |

Pitch framing: the demo today is hosted, pre-tiled 3D Tiles. The same scene
flows into the OpenUSD/Omniverse handoff documented in `honua-server-901`,
and future GPU-accelerated PDAL/cuSpatial executors run on the existing AWS
Batch / Azure Batch surface — `AwsBatchJobSubmission.GpuCount` is already in
the submission record.

## Storage and lifecycle

- Raw source files: `ICloudFileStorage` under `captures/{sessionId}/source/`.
  Local demo uses `LocalFileStorage`, no S3/Azure required.
- Derived tiles: must land on the scene server's filesystem `AssetRoot`
  (the path registered with the scene dataset) so the existing hosted
  serving path can read them through `SceneAssetResolver` and serve them
  via `Results.File`. The Phase 2 executor either writes directly to that
  mounted directory or syncs/promotes the converted tileset from blob
  storage; a cloud-object-backed scene asset provider that would let
  `SceneEndpoints` read tiles straight out of `ICloudFileStorage` is a
  separate follow-up and not assumed here. Once the tileset directory is
  in place, registering it with `SceneDataset` reuses the same access
  policy and cache headers as any other hosted scene.
- Cleanup: extend the existing file-cleanup background sweep (the same
  surface the upload pipeline already participates in) with a configurable
  retention for `Failed` and `Archived` capture sessions.
- Large-file uploads: rely on `StreamingFileUploadService` backpressure; do
  not introduce a new in-memory buffering path.

## Non-goals

- Implementing point-cloud conversion, COPC streaming, GPU/Batch executors,
  or CV inference in this spike.
- Adding native LAS/LAZ/COPC reader libraries to the server image in this
  spike.
- E57 ingest at any phase covered here.
- Web-client controls for capture-session selection or playback.
- OpenUSD, Omniverse, or Unreal export (covered separately by
  [`honua-server-901`](openusd-omniverse-export-path.md)).
- Buying or integrating drone-vendor APIs.
- A dedicated time-series store; `capturedAt` queries on `CaptureSession`
  are sufficient for Phase 1 evidence workflows.

## Risks and tradeoffs

- **`py3dtiles` / PDAL shell-out vs managed library.** `Process.Start`
  shell-out to `py3dtiles` (for `.pnts` emission) and PDAL (for upstream
  prep and COPC) keeps the server image AOT-safe and avoids heavyweight
  managed/native point-cloud dependencies, at the cost of requiring both
  tools in the Phase 2 executor's deployment environment. The existing
  AWS Batch / Azure Batch container model is the natural home — the
  server stays slim.
- **Pre-tiled vs server-side conversion.** Pre-tiled requires the operator
  to run a converter externally; this is the accepted Phase 1 tradeoff
  because it adds zero new code and zero cloud spend. COPC streaming is a
  cheap second slice if "upload one file and serve it" is required before
  Phase 2 conversion is ready.
- **COPC range-pass-through has no server-side LOD intelligence.** A thin
  HTTP-range adapter relies on the COPC client doing the right thing.
  Acceptable for a demo; a structured server-side reader is a Phase 3
  concern only.
- **`SceneDataset` vs new entity for capture.** Reusing `SceneDataset`
  alone loses capture semantics (timestamp, method, accuracy). The
  recommendation is to keep `SceneDataset` as the *serving* entity and
  introduce `CaptureSession` + `ProcessedAsset` as metadata wrappers that
  reference it. One FK join, two clean domains.
- **GPU pitch vs current capability.** The architecture supports adding a
  GPU executor without revisiting the ingest model — `AwsBatchJobSubmission`
  already exposes `GpuCount`. This is a credibility surface for the NVIDIA
  story; it is *not* implemented by this spike.
- **Coordinate / vertical datum handling.** Drone and scanner output
  arrives in many CRS (UTM zones, state planes, local frames). Phase 2
  must reproject explicitly; assuming EPSG:4326 silently is not
  acceptable. Vertical datum is often missing — record `unknown` rather
  than guessing.

## Pitch-safe language

Use this wording:

> Honua's first ingest path for drone, point-cloud, and reality-capture
> data is pre-tiled 3D Tiles served through the existing hosted scene
> infrastructure — the same path the NVIDIA construction demo fixture uses
> today. Server-side conversion of LAS/LAZ/COPC and GPU-accelerated tiling,
> segmentation, and change detection are documented as bounded follow-ups
> on the same job-execution surface that already supports AWS Batch and
> Azure Batch with optional GPU resource allocation.

Avoid this wording:

> Honua ingests LAS/LAZ point clouds.
>
> Honua runs GPU-accelerated point-cloud processing.
>
> Honua performs change detection on drone captures.
>
> Honua converts E57 scanner output.

## Proposed implementation sequence and child issues

These are bounded to `honua-server` unless noted, and each is sized for a
single AgentFlow ticket. None are filed by this spike; the spike's job is
to make them groomable.

| # | Title | Repo | Scope | Depends on | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | `feat(capture): capture-session metadata model + admin CRUD` | `honua-server` | New `CaptureSession` entity in `Honua.Core/Features/Scene/Domain/` (or sibling `Capture/Domain/`), Postgres schema and `internal` repository in `Honua.Postgres`, five admin routes following the `SceneDatasetEndpoints` pattern, integration tests with `WebAppFixture` + Postgres testcontainer, telemetry tags. | `honua-server-844` (scene dataset registry). | Proposed; not filed by this spike. |
| 2 | `feat(capture): processed-asset model + scene-dataset linkage` | `honua-server` | New `ProcessedAsset` entity, FK to `CaptureSession` and `SceneDataset`, `POST /api/v1/admin/capture-sessions/{id}/processed-assets`, repository, integration tests. | Child #1. | Proposed; not filed by this spike. |
| 3 | `feat(upload): allow LAS/LAZ/COPC MIME types in FileUploadSecurity` | `honua-server` | Extend allowed MIME list with the agreed canonical set; add `LASF` magic-byte sniff; unit tests for new MIME policies; no new endpoints. | Independent. | Proposed; not filed by this spike. |
| 4 | `feat(scene): COPC streaming adapter` | `honua-server` | A range-aware route or content-type branch (`GET /scenes/{sceneId}/{*assetPath}` already accepts `.bin`; add the COPC suffix to the type table or a dedicated route). HTTP `Range` forwarding through the existing static-file pipeline. Output-cache policy for COPC hierarchy nodes. Endpoint tests. | Child #3. | Proposed; not filed by this spike. |
| 5 | `feat(jobs): point-cloud ingest CPU executor (py3dtiles + PDAL shell-out)` | `honua-server` | New `IJobExecutor` for `ExecutionJobKind.PointCloudIngest`. Shell-out via `Process.Start` to `py3dtiles` for `.pnts` 3D Tiles and to PDAL for upstream prep / COPC. Writes the converted tileset onto the scene-server's filesystem `AssetRoot` (or a sync/promotion target), then registers via `ISceneRegistrationService`. Progress to `IExecutionLogStore`. Integration test with a small synthetic LAS fixture. | Children #1, #2, #3. | Proposed; not filed by this spike. |
| 6 | `feat(jobs): GPU-capable Batch submission for PointCloudIngestGpu` | `honua-server` | Add `GpuInstanceType` (or `GpuCount`) to `ExecutionJobSpec`; wire `AwsBatchJobSubmission.GpuCount` and the Azure equivalent through the spec; new `ExecutionJobKind.PointCloudIngestGpu`; container image selection for CUDA-enabled PDAL. | Child #5. | Proposed; not filed by this spike. |
| 7 | `feat(jobs): change-detection executor (Phase 3 contract)` | `honua-server` | Define the interface contract now: accept two `CaptureSessionId` values plus a diff metric; new `ExecutionJobKind.ChangeDetection`. Implementation can stub initially and wire to cuSpatial later. | Child #6 plus design grooming. | Proposed; not filed by this spike. |

## Open questions for grooming

These echo the design brief's review questions and remain unresolved. They
should be answered before child #1 is filed:

1. **Source-file storage**: Honua-managed `ICloudFileStorage` for raw
   multi-GB LAS/LAZ, or operator-supplied URI references (`sourceUri` only)?
   The metadata model differs slightly between the two answers.
2. **Project scoping**: `projectId` as a free-form string for now, or a FK
   into a future `Project` entity? Today no `Project` entity exists.
3. **COPC streaming as Phase 1**: keep it deferred to child #4, or bundle a
   thin range-pass-through into the first slice so operators can serve a
   single `.copc.laz` without pre-tiling?
4. **Change-detection output shape**: a vector overlay served as a
   `SceneDataset`, or a scalar/report record only? Determines whether
   child #7 produces a tileset.
5. **Sales coordination**: are there format commitments already in flight
   on `honua-sales-36` (for example a customer locked in on E57) that
   should reorder the matrix?

## Acceptance-criteria mapping

| Acceptance criterion (from `honua-server-900`) | Where addressed |
| --- | --- |
| Recommended first supported formats are documented with tradeoffs. | "Format decision matrix" lists every requested format with cost, storage, NVIDIA path, and notes. |
| A demo ingest path is chosen: pre-tiled assets, local conversion, or server-side job. | "Recommendation" and "Demo ingest path" choose pre-tiled assets registered through the existing scene dataset registry. |
| NVIDIA relevance is explicit: GPU acceleration, tiling, reconstruction, segmentation, change detection, or simulation. | "NVIDIA relevance (explicit)" maps each capability to a tool, phase, and Honua surface. |
| Follow-up implementation issues are specific enough for AgentFlow. | "Proposed implementation sequence and child issues" lists seven bounded tickets with repo, scope, dependencies, and status. |
| The recommendation does not require cloud spend for the first local demo. | "Demo ingest path (local, no cloud spend)" runs on `LocalFileStorage` and the existing hosted serving routes; no Batch, S3, Azure, or Cesium ion call is made. |

## References

- [OGC 3D Tiles 1.1 specification](https://www.ogc.org/standard/3dtiles/)
- [3D Tiles `pnts` point-cloud tile format (1.0)](https://github.com/CesiumGS/3d-tiles/tree/main/specification/TileFormats/PointCloud)
- [COPC specification](https://copc.io/)
- [PDAL — Point Data Abstraction Library](https://pdal.io/) (writers list: COPC/GLTF/LAS/etc., no 3D Tiles writer; reprojection, denoising, decimation, COPC output)
- [py3dtiles](https://py3dtiles.org/) (LAS/LAZ → `tileset.json` + `.pnts` 3D Tiles converter — the open-source path used by the proposed Phase 2 executor)
- [PotreeConverter](https://github.com/potree/PotreeConverter) (emits Potree-native `metadata.json` octree, **not** OGC 3D Tiles; only relevant if a Potree-serving adapter is later added — out of scope for this spike)
- [OpenDroneMap](https://www.opendronemap.org/)
- [NVIDIA cuSpatial](https://docs.rapids.ai/api/cuspatial/stable/)
- Honua scene references: [Hosted 3D Tiles Scenes](scenes-3dtiles.md),
  [3D Tiles Generation Pipeline](scene-generation.md),
  [Scene Dataset Registry (Admin API)](../admin-api/scene-dataset-registry.md),
  [NVIDIA Construction Demo Fixture](../demo/nvidia-construction.md),
  [OpenUSD and Omniverse Export Path](openusd-omniverse-export-path.md).
