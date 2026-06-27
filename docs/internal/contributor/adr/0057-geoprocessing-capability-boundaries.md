# ADR-0057: Geoprocessing capability boundaries (server-canonical engine, thin SDKs, cloud-delegated ML)

## Status

Accepted (2026-06)

## Context

Honua's geoprocessing (GP) capability now spans more than the server. Alongside
the canonical server engine, the published SDKs (`honua-sdk-python`,
`honua-sdk-dotnet`) and the AI/MCP surface all expose "do GP" affordances, and
the first-release backlog adds raster map-algebra, proximity/terrain tools, and
an imagery/ML lane (honua-io/honua-server#2239, #2240, #2241). A holistic review
across the three layers surfaced architectural questions that the existing GP
records do not answer:

- [ADR-0026](0026-ai-first-operator-contract.md) establishes the AI-first
  operator contract as the primary public contract.
- [ADR-0029](0029-geoprocess-canonical-model-mappings.md) establishes a single
  canonical process model with **protocol adapters** (GPServer, OGC API
  Processes) projecting from it, and forbids adapters introducing new domain
  types into `Honua.Core`.

Both are scoped to the **server**. Neither answers:

1. **Where GP computation is allowed to live.** May the SDKs run geoprocessing
   client-side (Shapely/geopandas in Python, NetTopologySuite/GDAL in .NET), or
   must they delegate to the canonical server engine? Today the Python SDK is
   already a thin OGC API Processes client (its `honua-arcpy`/compat analysis
   surface is stubbed), and the .NET SDK has a small genuine local **vector**
   tier (`Honua.Sdk.Geometry`, NTS/ProjNet). Without a decision, both are free to
   grow into parallel GP engines.
2. **How machine-learning / imagery analysis is delivered.** GDAL gives raster
   I/O and arithmetic but no learning; ArcGIS-style image classification needs a
   model runtime. Do we bundle one (scikit-learn / PyTorch / ONNX) into the
   server or SDKs, or delegate to managed cloud inference?
3. **Whether distributed analysis is in scope** for first release.

If left implicit, the predictable failure mode is **three divergent GP
implementations** — server, Python, .NET — that disagree on buffers, overlays,
CRS handling, and edge cases. That is the exact drift the "thin adapters over one
canonical pipeline" rule exists to prevent, now leaking past the server boundary
into client libraries.

## Decision

### 1. One canonical GP engine: the server

The **only** geoprocessing engine is the server's canonical process runtime
(ADR-0029), reached through OGC API Processes (and the GPServer compat adapter).
All protocol surfaces and all SDKs are clients of that one engine. New analytical
capability is added as a registered `ProcessDefinition` in `IProcessCatalog`, not
as client-side code, so every surface — Python, .NET, MCP/agents, GeoServices —
inherits identical behavior and results.

### 2. SDKs are thin clients; no parallel client-side GP engine

SDKs **must not** reimplement geoprocessing algorithms (buffer, overlay,
interpolation, raster math, density, clustering, …). Their job is:

- a **process client** (submit / poll / fetch results against OGC API Processes), and
- **interop / last-mile** helpers that move canonical results into the user's own
  ecosystem.

Specifically:

- **Python SDK** — interop, not engine. Results convert to `GeoDataFrame`
  (vector) and `rioxarray`/`xarray`/`rasterio` (raster) behind optional extras;
  the compat surface is un-stubbed by **delegating to server processes**, not by
  computing locally (honua-io/honua-sdk-python#124). Users who want ad-hoc local
  analysis use their native libraries (geopandas/shapely/pysal/scikit-learn)
  directly — the SDK does not wrap them as a competing engine.
- **.NET SDK** — thin client **plus** the existing local **vector** convenience
  tier (`Honua.Sdk.Geometry`: measure/buffer/simplify/predicates/nearest/
  geofence/CRS transform via NTS/ProjNet) for genuinely offline/edge scenarios
  where no server is reachable. This local tier is a documented convenience with
  a stated parity boundary, not a second canonical engine; when a server is
  reachable, GP goes to the server. Offline **raster** in .NET (GDAL bindings)
  is **deferred** (mobile/edge only) and is not first-release scope.

### 3. Machine learning / imagery analysis is cloud-delegated, never bundled

Imagery/ML GP (classification, segmentation, object detection) is delivered as a
server GP lane that **delegates to managed cloud inference** (Amazon SageMaker,
Azure ML, Google Vertex AI, or a generic hosted-ONNX/REST endpoint) behind one
provider-pluggable interface (honua-io/honua-server#2241). Models, accelerators,
and training stay in managed services. No model runtime
(scikit-learn/PyTorch/ONNX-runtime) is bundled into the server or SDKs, and no
GPU dependency enters the baseline image. With no backend configured the lane
advertises itself unavailable with a clear message (no silent stub). Credentials
resolve through the existing secure-connection/secret mechanism.

### 4. Native heavy GP leans on the deployed GDAL worker

Raster/terrain GP is implemented by wiring utilities already shipped in the
native GDAL worker (`gdal_calc`, `gdal_grid`, `gdal_contour`, `gdal_proximity`,
`gdal_viewshed`, `gdal_polygonize`/`gdal_rasterize`, `gdaldem`) into canonical
processes, rather than adding new numerical dependencies. (Consistent with the
lean-image constraint from [ADR-0038](0038-geoetl-pipeline-architecture-and-runtime-boundary.md).)

## Scope Out

- **Distributed / cluster geoprocessing.** First release is single-node GDAL/NTS
  job execution. Distributed analysis (Spark/Dask/GeoAnalytics-style) is
  deferred; where GP jobs run at scale is addressed operationally by serverless
  provisioning (honua-io/honua-server#2165), not by a distributed compute model
  in the engine.
- **In-process ML / GPU.** See Decision 3 — delegated, not bundled.
- **Bundling third-party analysis libraries into SDKs as a re-exported engine.**
  Interop helpers are allowed; wrapping geopandas/PySAL/scikit-learn as a Honua
  GP engine is not.
- **True kriging and inferential spatial statistics** (GWR, Moran's cluster/
  outlier) beyond what is already tracked (kriging is library-gated in
  honua-io/honua-server#2141; HotSpot Gi*/KDE in #2142) — deferred past first
  release.

## Consequences

- One source of truth for GP behavior; identical results across every protocol
  and SDK. Adding a tool once exposes it everywhere.
- SDK maintenance stays bounded — clients track the process catalog instead of
  re-implementing and re-testing geometry/raster math per language.
- The ML story ships without a heavyweight model runtime or GPU in the baseline,
  at the cost of a configured cloud backend being required for imagery ML (an
  accepted trade for first release).
- The .NET local vector tier is a deliberate, narrow exception; it carries a
  documented parity boundary and the risk that its NTS results differ in edge
  cases from the server. This is accepted for offline/edge value and bounded to
  vector geometry only.
- Consumers needing distributed or in-house ML must wait for a later scope; the
  cloud-delegation seam is designed so that adding such backends later does not
  change the public GP contract.

## References

- [ADR-0026: AI-First Operator Contract](0026-ai-first-operator-contract.md)
- [ADR-0029: Geoprocess Canonical Model Mappings](0029-geoprocess-canonical-model-mappings.md)
- [ADR-0038: GeoETL Pipeline Architecture and Runtime Boundary](0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
- Epic: honua-io/honua-server#1259 (port Esri GP services) — holistic plan in its comments
- honua-io/honua-server#2239 (raster map-algebra + spectral indices), #2240
  (proximity/terrain pack), #2241 (imagery/ML cloud delegation)
- honua-io/honua-sdk-python#123 (rename `honua-arcpy` → `honua-gp`), #124 (Python
  GP = interop + process client)
