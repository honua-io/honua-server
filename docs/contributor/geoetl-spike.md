# GeoETL — Competitor Evaluation and Product Strategy Spike

**Ticket**: #682
**Date**: 2026-04-19
**Related**: #361 (epic), #681 (worker substrate, merged), #374 (enrichment)
**Scope**: Strategy and positioning — no code deliverables

---

## 1. Demand Validation

GeoETL is a strategic category bet, not a pull from one named deal. The primary demand signal is displacement economics: FME enterprise licenses price at ~$10K+/seat/year, and spatial ETL sits in the evaluation when customers move off Esri or off FME. The demand shape is "repeatable, scheduled, multi-source pipeline," not "graphical workbench parity."

Opportunity conversations consistently ask three questions:

1. Can it read from *my* sources (usually Esri REST, PostGIS, an OGC API, and files on S3/Azure Blob)?
2. Can it run on a schedule or on upload, without me owning a pipeline server?
3. Can I version the pipeline in Git?

They do not ask for a desktop canvas. They do not ask for every GDAL driver. That shape should govern scope.

---

## 2. Competitor Matrix

| Category | Strengths to respect | Failure modes to avoid | Lesson for Honua |
|---|---|---|---|
| **FME / Safe Software** | Driver breadth; projection handling; mature workbench; enterprise ETL depth; vendor-signed format support. | Desktop-first authoring; per-seat pricing; heavy runtime; "transformer sprawl"; migration from FME is a project in its own right. | Do not clone the workbench. Compete on API-first authoring, Git-versioned pipelines, and runtime economics. |
| **ArcGIS Data Interoperability** | Tight Esri integration; understands Esri semantics end-to-end. | Locks the pipeline into the Esri stack; the pipeline itself is not portable. | Treat Esri REST as a first-class source *and* sink — we inherit migration customers, pipelines stay portable. |
| **QGIS Processing / Graphical Modeler** | Free; GDAL/GRASS/SAGA under the hood; familiar to analysts. | Desktop-bound; not a server product; weak for scheduled multi-tenant use. | Lean GDAL/OGR for unglamorous format work. Do not try to be a QGIS replacement — that fight is on the desktop. |
| **GeoKettle / Pentaho-style** | Workflow-first model; open-source orchestration heritage. | Largely unmaintained; JVM operational footprint; scant modern cloud story. | Validates that "ETL as pipeline" is a real mental model. Does not validate a JVM stack or a visual-first IDE. |
| **Wherobots / Apache Sedona** | Distributed geospatial compute; lakehouse-scale joins and analytics. | Heavy infrastructure; requires Spark/K8s maturity; overkill for the 80% case. | Stay out of Spark-scale lakehouse until a customer explicitly needs it. Keep the pipeline contract portable so a Sedona executor could slot in later. |
| **Developer-first stack (GDAL, PostGIS, dbt, GeoPandas)** | Maximum flexibility; no vendor risk; already in use at most sophisticated shops. | Glue code; no scheduling; no tenancy; no lineage; every team reinvents it. | Ship the "packaged version of what a competent team would build themselves." Don't fight the primitives — *use* PostGIS and GDAL, wrap them with orchestration, observability, and edition-aware packaging. |
| **GeoETL (Rust / DataFusion / Arrow)** | Modern columnar engine; credible performance story; aligned with vector cloud direction. | Young; narrow connector set; unclear enterprise narrative; niche format coverage. | Watch, do not chase. Vectorized columnar is a Phase 2+ conversation once the baseline pipeline contract is stable. |

---

## 3. Strategic Positioning

Honua GeoETL is the **API-first, Git-versioned, protocol-aware spatial ETL runtime** for teams that already have a geospatial server and do not want a desktop pipeline tool.

Positioning statement:

> Honua GeoETL turns spatial data movement into pipeline-as-code. Declarative pipelines pull from the sources teams actually use — Esri REST, PostGIS, OGC API Features, cloud object stores — apply curated transforms and enrichment, and load into Honua layers or external PostGIS. Pipelines run on a schedule or on upload, are versioned in Git, and produce deterministic logs and replay artifacts. No desktop application, no per-seat license, no Spark cluster required.

Three deliberate "nots":

- **Not a desktop workbench.** FME already owns that market, and the desktop model is the reason FME is expensive. Authoring is pipeline-as-code first; a light admin UI for monitoring and dry-run comes later.
- **Not a distributed lakehouse engine.** Sedona/Wherobots already occupy that end. We keep the contract portable so an external executor could be added when a customer proves the need.
- **Not a promise to match every GDAL driver.** A curated set that covers the common production 80% is the product; the long tail is custom code or a follow-on release.

---

## 4. Answers to the Questions Posed by #682

**What should Honua do natively vs. delegate?**
Native: pipeline definition, scheduling, execution state, lineage, replay, protocol-aware loading into Honua layers, attribute transforms, filtering, dedup, enrichment via spatial join. Delegate: format I/O (GDAL/OGR for less-common formats), heavy spatial transforms where PostGIS is the right engine (reproject at scale, ST_Buffer over large tables, spatial joins), distributed analytics (deferred to an optional external executor, not in baseline).

**Baseline source/target set (80% target).**
Sources: GeoJSON, Shapefile, GeoPackage, CSV-with-coords, KML, PostGIS (remote), Esri REST feature services, OGC WFS, OGC API Features, S3 / Azure Blob / GCS file watchers, generic REST/JSON with a simple mapping DSL. Sinks: Honua layers (create / append / upsert), external PostGIS, GeoJSON, GeoPackage, Shapefile. Everything else is Phase 2+ or custom code.

**What belongs in the first-class transform / enrichment surface?**
First-class stages: reproject, simplify, validate/repair, clip-to-AOI, rename/cast/concat/split/regex, attribute filter, spatial filter, spatial join, lookup/join, dedup, null-fill, outlier detection, coordinate precision clamp, reverse geocode, spatial join enrichment per #374. Deferred to PostGIS or custom: network analysis, raster algebra, topology rules, routing, clustering, complex geometry constructions.

**User experience.**
API-first and pipeline-as-code from day one. A light admin UI for pipeline listing, execution monitoring, error inspection, and dry-run is table stakes. A visual pipeline builder is explicitly Phase 3 and is not a launch requirement. Pipelines are YAML/JSON, checked into Git, and scheduled through the control plane.

**How GeoETL exploits #681.**
The substrate is already merged: `IJobQueue` (atomic claim, heartbeat, requeue), `ExecutionJobKind.ExtractTransformLoad` (already reserved), `OperationPriority`, `IExecutionJobStore`. GeoETL does not introduce a new queue. It introduces a new worker profile — `honua-worker-etl` — that subscribes to `ExtractTransformLoad` jobs and ships with GDAL/OGR and any heavyweight native deps baked in. The default serving image stays lean.

**Edition boundaries.**
Community: one-shot import (existing; never degraded), bundled open-data enrichment (Natural Earth, timezones). Pro: scheduled pipelines, full connector set, full transform library, enrichment against premium datasets, pipeline versioning and rollback, admin UI. Enterprise: streaming sources (MQTT, webhook), custom transform plugins, cross-tenant pipelines, pluggable distributed executors, SSO-bound pipeline RBAC.

**Proof workloads.**
(Section 8.)

---

## 5. Runtime and Deployment Architecture

Two worker profiles, one substrate:

- **`honua-server` (default serving image)**: Minimal APIs, control plane, submission endpoints. No GDAL/OGR. Must stay within the existing ECS/serverless cold-start and memory profile.
- **`honua-worker-etl` (heavyweight ETL profile)**: Consumes `IJobQueue` with `AcceptedKinds = { ExtractTransformLoad }`. Ships GDAL/OGR, native projections data, any format-specific binaries. Deployed as a separate ECS task definition / Kubernetes Deployment / Container Apps Job. Never reachable from the public ingress.

Pipeline definition flow:

1. Pipeline YAML/JSON submitted via API or applied via GitOps (#351).
2. Control plane validates, versions, and stores the pipeline.
3. Scheduler (cron / event trigger from #316 CDC / file-upload event) enqueues an `ExtractTransformLoad` job.
4. An ETL worker claims, executes stages in order, emits structured per-row error tracking, writes artifacts (rejected rows, lineage snapshot, replay bundle) to object storage.
5. Terminal status and artifact references reconcile back through the existing `IExecutionJobStore`.

Portability guarantees: pipeline definitions, scheduling semantics, logs, and replay bundles must be identical across executor backends. This keeps the door open for a Kubernetes Jobs / AWS Batch / Azure Container Apps Jobs / Sedona executor later without breaking authored pipelines.

---

## 6. MVP Scope

**Connectors (Pro, first wave):**
GeoJSON, Shapefile, GeoPackage, CSV-with-coords, KML, PostGIS (remote), Esri REST feature services, OGC WFS, OGC API Features, S3 / Azure Blob / GCS file watchers, generic REST/JSON with mapping DSL. Sinks: Honua layers, external PostGIS, GeoJSON, GeoPackage, Shapefile.

**Transforms (Pro, first wave):**
Geometry: reproject, simplify, clip-to-AOI, validate/repair. Attributes: rename, cast, concat, split, regex extract, lookup/join. Filtering: attribute filter, spatial filter, dedup (spatial + attribute).

**Enrichment / QA library (Pro, first wave, coordinated with #374):**
Spatial-join enrichment against bundled Natural Earth (Community tier) and premium datasets (Pro/Enterprise). Reverse geocode. Null-fill. Outlier detection. Coordinate precision clamp. Row-level error capture with quarantine sink.

**Orchestration (Pro):**
Declarative YAML/JSON pipelines, cron schedule, file-upload trigger, CDC trigger from #316, pipeline versioning, execution history, dry-run, row-level error inspector, admin UI for execution monitoring.

**Explicitly deferred (Phase 2+):**
MQTT / webhook streaming sources, custom transform plugins, cross-tenant pipelines, visual pipeline builder, distributed executor backends, long-tail GDAL drivers, raster ETL, network-analysis transforms.

---

## 7. Phased Plan

**Phase 1 — Pipeline contract and lean connector set.** Pipeline YAML schema, pipeline CRUD + versioning API, ETL worker profile image, `ExtractTransformLoad` executor wiring on top of #681, connectors for GeoJSON / Shapefile / GeoPackage / PostGIS / Esri REST / OGC API Features / object-store file watcher, core geometry and attribute transforms, row-level error capture, execution history, dry-run. **Non-goal:** admin UI, enrichment catalog, streaming sources.

**Phase 2 — Enrichment, UX, operational polish.** Enrichment library coordinated with #374, remaining MVP connectors (KML / CSV / WFS / generic REST), admin UI for execution monitoring and error inspection, cron scheduler, CDC trigger integration (#316), GitOps-applied pipelines (#351). **Non-goal:** streaming, plugins, distributed executors.

**Phase 3 — Enterprise surface.** Streaming sources (MQTT, webhook), custom transform plugins (sandboxed), cross-tenant pipelines, pluggable executor backends (Kubernetes Jobs, AWS Batch, Azure Container Apps Jobs). **Non-goal:** desktop canvas, Sedona-scale lakehouse.

---

## 8. Proof / Sample Workloads

Before large implementation investment, these workloads validate the approach:

1. **Esri REST → Honua layer, nightly.** Pull a public Esri FeatureServer layer on cron, reproject, clip to AOI, upsert into a Honua layer. Validates the Esri displacement narrative.
2. **S3 drop → Honua layer, on-upload.** Watch an S3 prefix for GeoPackage uploads, validate/repair geometry, append to a Honua layer, quarantine rejected rows to a sidecar prefix. Validates the event-driven / object-store operational story.
3. **PostGIS → GeoPackage, scheduled export.** Query a remote PostGIS table, enrich with Natural Earth boundary attributes, write a GeoPackage to object storage. Validates the "PostGIS-first spatial transforms + curated enrichment" positioning.
4. **Inline GeoJSON → Honua layer with enrichment (#374 integration).** POST features to the enrichment API, spatial-join against a premium dataset, return enriched features. Validates the API-first authoring surface and the enrichment integration.

Each workload is scriptable, runs against a real heavyweight worker profile, and produces a replay bundle.

---

## 9. Non-Goals

- No desktop pipeline workbench. FME owns that shape; we compete differently.
- No distributed Spark/Sedona lakehouse executor in the baseline. The contract stays portable; the executor is a future optional plug-in.
- No blanket GDAL driver promise. Curated 80% coverage first.
- No GDAL/OGR in the default serving image. Heavyweight deps live in the ETL worker profile.
- No AWS Batch / Azure Batch dependency in the baseline. Those are optional executor backends, not delivery prerequisites.
- No bespoke durable-job substrate for ETL. #681 is the substrate; ETL is a job kind on it.

---

## 10. Recommendation

Proceed with Phase 1 as scoped above. The positioning is defensible, the substrate dependency (#681) is already in place, and the MVP connector/transform/enrichment set covers the common 80% without inviting scope explosion. Revisit distributed executor plug-ins, streaming sources, and a visual pipeline builder only when a named-account ask or pilot blocker justifies the investment.
