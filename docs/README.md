# Honua Server Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**

**New here?** Start with the [Platform Overview](concepts/architecture.md) for architecture, protocols, and capabilities.
**Need to defend a claim?** The [Evidence Index](internal/evidence/README.md) is the cross-cutting map of compatibility, conformance, parity, certification, and migration evidence across the repo.
Historical planning, audit, and design artifacts live under [docs/archive/](archive/README.md) and are not part of the current product contract.

This page is the canonical table of contents for every important doc in the repo. If you can't find something here, it is either archived under [docs/archive/](archive/README.md) or it does not exist yet — open an issue.

## By Role

| I am a... | Start here |
|---|---|
| **Server Operator** | [Operator Guide](archive/role-indexes/operator-README.md) — deploy, configure, monitor, manage |
| **GIS Professional** | [GIS User Guide](archive/role-indexes/gis-README.md) — connect desktop apps, consume services |
| **Developer** | [Developer Guide](archive/role-indexes/developer-README.md) — APIs, SDKs, integrations |
| **Contributor** | [Contributor Guide](internal/contributor/README.md) — architecture, testing, PRs |

## Quick Links

| I want to... | Go to |
|---|---|
| Deploy the server | [Infrastructure](guides/deploy/kubernetes.md) / [Docker Compose](guides/deploy/docker-compose.md) |
| Connect QGIS | [QGIS Tutorial](guides/connect/qgis.md) |
| Connect ArcGIS Pro | [Client Templates](gis/CLIENT_TEMPLATE_RUNBOOK.md) |
| Manage services via API | [Control Plane API](reference/admin-api/overview.md) |
| See API examples | [API Examples](guides/query-analyze/query-features.md) |
| Check protocol support | [Protocols Overview](concepts/protocols.md) |
| Serve terrain/elevation tiles | [Terrain-RGB Tiles](guides/publish/publish-terrain-and-elevation.md) |
| Look up numeric elevation values | [Elevation Query and Profile API](reference/protocols/terrain-and-elevation.md) |
| Integrate AI agents | [MCP Server](guides/connect/ai-agents-mcp.md) |
| Validate packages before publish/execute | [Package Review API](internal/developer/package-review-api.md) |
| Troubleshoot issues | [Troubleshooting](guides/deploy/troubleshooting.md) |
| Review OpenAPI specs | [API Specs](developer/api-specs) |

## Standards & Compliance

The authoritative claims about what Honua conforms to, and the evidence behind them.

- [API Standards Summary](reference/compatibility/ogc-conformance.md) — consolidated map of OGC CITE pass rates, gRPC versioning policy, OpenAPI drift workflow, and API versioning strategy.
- [CITE Status](cite-status.md) — authoritative snapshot of OGC CITE pass rates per protocol on `trunk` (currently **952 / 952** across 11 suites).
- [OGC CITE Conformance Evidence](internal/contributor/ogc-cite-conformance-evidence.md) — canonical, website-linkable summary with per-suite totals and evidence links.
- [Standards & APIs Overview](concepts/protocols.md) — every standard and protocol Honua speaks, with endpoint, version, and coverage links.
- [OGC Certification Path](internal/contributor/ogc-certification-path.md) — what we have certified, what is in flight, and what is next.
- [CITE Runbook](internal/contributor/cite-runbook.md) — how to run CITE locally and regenerate evidence bundles.
- [gRPC Versioning Policy](reference/protocols/grpc.md) — how the `Geospatial.V1` gRPC surface is versioned and what stability guarantees we make.
- [Control Plane Versioning Policy](reference/versioning-and-support.md) — versioning and stability guarantees for the admin / control-plane API.
- [Control Plane Migration Guide](reference/control-plane-migration-guide.md) — how to migrate clients across control-plane API versions.
- [MVP Compatibility Contract](reference/compatibility/clients.md) — the supported client × protocol matrix for MVP.
- [Public Interface Quality Model](internal/contributor/public-interface-quality-model.md) — how we score and gate public surfaces.
- [Package and Module Governance](internal/contributor/package-and-module-governance.md) — how repo packages and modules are kept stable.

## Operations & Security

How to deploy, run, harden, and respond to incidents.

### Operator handbook

- [Operator Guide (index)](archive/role-indexes/operator-README.md)
- [Infrastructure](guides/deploy/kubernetes.md) — supported runtimes, sizing, IaC notes.
- [Docker Compose](guides/deploy/docker-compose.md) — local + production-like compose stacks.
- [Operations](guides/deploy/operations.md) — day-2 operational tasks.
- [Monitoring](guides/deploy/monitoring.md) — metrics, traces, and SLO surfaces.
- [Troubleshooting](guides/deploy/troubleshooting.md)
- [Security](guides/secure/authentication.md) — operator-facing hardening guidance.
- [Client Certificate Authentication](guides/secure/client-certificate-authentication.md) — native/admin mTLS modes, trust profiles, mappings, revocations, and response contracts.
- [TLS Connection Guide](guides/secure/tls-and-mtls.md)
- [HTTP Client Resilience](guides/deploy/http-client-resilience.md)
- [Tile Operations Runbook](guides/publish/publish-tiles.md)
- [Control Plane API](reference/admin-api/overview.md)
- [Deployment Scenarios](guides/deploy/deployment-scenarios.md)
- [Database Support Matrix](reference/configuration/data-sources/README.md)
- [Migration Pilot Cutover Checklist](guides/migrate/migration-pilot-cutover-checklist.md)
- [Migration Toolkit](guides/migrate/from-arcgis-server.md)
- [SLD Migration](guides/style/import-sld-styles.md)
- [OGC API Features Migration](archive/operator/ogc-api-features-migration.md)
- [OGC Coverage Migration](archive/operator/ogc-coverage-migration.md)
- [PMTiles Publishing](guides/publish/pmtiles-publishing.md)
- [Feature Streaming](guides/edit/feature-streaming.md)
- [Feature Change Webhooks](guides/edit/react-to-changes.md)
- [ArcGIS Inventory Discovery](guides/migrate/arcgis-inventory-discovery.md)

### Provider guides

- [DuckDB Provider](reference/configuration/data-sources/duckdb.md)
- [MySQL / MariaDB Provider](reference/configuration/data-sources/mysql-mariadb.md)
- [Oracle Provider](reference/configuration/data-sources/oracle.md)
- [SQL Server Provider](reference/configuration/data-sources/sql-server.md)

### Runbooks

- [Operator Runbooks (index)](archive/role-indexes/operator-runbooks-README.md)
- [License Key Rotation](reference/admin-api/license-key-rotation.md)
- [License Migration](concepts/license-migration.md)
- [Marketplace Operations](internal/operator/MARKETPLACE_OPERATIONS.md)
- [Upgrade and Rollback](guides/deploy/upgrade-and-rollback.md)

### Security

- [Security Policy](../SECURITY.md) — supported versions, vulnerability reporting, disclosure process. **(repo root)**
- [Compliance Framework](guides/secure/compliance.md) — SOC 2 / FedRAMP readiness evidence collection, data residency policy + dry-run, compliance key-version rotation events, and report export.
- [Base URL & Open Redirect Handling](internal/security/base-url-and-open-redirect-handling.md)
- [Code Scanning — 2026 Q2 Remediation](internal/security/code-scanning-2026-Q2-remediation.md)
- [Security-First File Upload Design (ADR 0019)](internal/contributor/adr/0019-security-first-file-upload-design.md)

## Architecture & Design

Strategic direction, the canonical architecture, and the decisions behind it.

- [AGENTS.md](../AGENTS.md) — repo-wide guide for human and AI agents working on the codebase. **(repo root)**
- [Platform Overview](concepts/architecture.md) — what Honua is and how the pieces fit together.
- [Architecture (contributor)](internal/contributor/ARCHITECTURE.md)
- [Architecture Diagrams](internal/contributor/ARCHITECTURE_DIAGRAMS.md)
- [Architecture Criteria](internal/contributor/architecture-criteria.md)
- [Honua Manifesto](internal/contributor/HONUA_MANIFESTO.md)

### Architecture notes

- [Admin Operator Workflows](internal/contributor/architecture/admin-operator-workflows.md)
- [Configuration and Secrets](internal/contributor/architecture/configuration-and-secrets.md)
- [Metadata v2 — Admin UI Information Model](internal/contributor/architecture/metadata-v2-admin-ui-information-model.md)
- [Metadata v2 — Release Readiness](internal/contributor/architecture/metadata-v2-release-readiness.md)
- [Unified License and Entitlement](internal/contributor/architecture/unified-license-and-entitlement.md)

### Architecture Decision Records

- [ADR Index](internal/contributor/adr/README.md) — every accepted ADR, in numeric order. Recent additions:
  - [ADR 0036 — Mobile SDK Language Strategy](internal/contributor/adr/0036-mobile-sdk-language-strategy.md)
  - [ADR 0037 — Unified CI Test Tier Strategy](internal/contributor/adr/0037-unified-ci-test-tier-strategy.md)
  - [ADR 0038 — GeoETL Pipeline Architecture and Runtime Boundary](internal/contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
  - [ADR 0039 — Cloud-Optimized HDF/NetCDF Reader Strategy](internal/contributor/adr/0039-cloud-optimized-hdf-netcdf-reader-strategy.md)

## Developer Guides

How to build against Honua's APIs, SDKs, and protocols.

- [Developer Guide (index)](archive/role-indexes/developer-README.md)
- [API Examples](guides/query-analyze/query-features.md)
- [Integration Patterns](reference/integration-patterns.md)
- [Capability Manifest](reference/admin-api/capability-manifest.md) — neutral runtime capability discovery for Console, MCP, QGIS, native hosts, and SDK clients.
- [Grounding](internal/developer/GROUNDING.md) — natural-language grounding for AI builders.
- [MCP Server](guides/connect/ai-agents-mcp.md) — JSON-RPC surface for AI agents.
- [Package Review API](internal/developer/package-review-api.md) — shared validation and read-only preview planning contract for publish/execute candidates.
- [Share Export and Traffic API](internal/developer/share-export-traffic-api.md) — Console Share scheduled exports, run history with Operate job links, and traffic projections.
- [Redis Fallback Patterns](internal/developer/REDIS_FALLBACK_PATTERNS.md)
- [Spec Engine](reference/spec-engine.md)
- [AI Builder SDK Contract](internal/developer/ai-builder-sdk-contract.md) — Map of honua-server MCP surfaces to the honua-sdk-js AI Spatial App Builder workflow, fixture cases, and capability states.
- [AI Builder Contract Fixtures](internal/developer/ai-builder-contract-fixtures.md)
- [Form Package API](reference/admin-api/forms.md) — Versioned form package drafts, validation, immutable publishing, offline policy discovery, and field submission contracts.
- [SDK Compatibility Matrix](concepts/ecosystem.md)
- [SDK Compatibility Metadata](internal/developer/SDK_COMPATIBILITY_METADATA.md)
- [SDK Standards Coverage](internal/developer/SDK_STANDARDS_COVERAGE.md)
- [SDK Migration Template](internal/developer/sdk-migration-template.md)
- [Mobile SDK Roadmap](internal/developer/mobile-sdk-roadmap.md)
- [FieldCollection Mobile Sync API](internal/developer/fieldcollection-mobile-sync-api.md)
- [Mobile Offline Demo Fixture](internal/developer/mobile-offline-demo-fixture.md)
- [Metadata Catalog Parity Matrix](internal/developer/metadata-catalog-parity-matrix.md)
- [Spec Grammar v1.0](developer/spec-grammar/v1.0/README.md)
- [Spec Grounding v1.0](developer/spec-grounding/v1.0/README.md)
- [API Specs (OpenAPI bundles)](developer/api-specs/README.md)

### Admin API

- [Console Content, Share, and RBAC (Baseline)](internal/admin-api/console-content-and-rbac.md) — Metadata v2 content item, session bootstrap, action-check, provenance, Share access, public-link, and embed endpoints under `/api/v1/console/**` (#1162, #1215).
- [Console Workflow Packages](internal/admin-api/console-workflow-packages.md) — Server-owned node registry, mutable workflow package drafts, immutable versions, validation, dry-run, publication, runs, and provenance under `/api/v1/console/workflow-*` (#1185).
- [Console Job Observability](internal/admin-api/console-job-observability.md) — Durable execution job list, detail, logs, artifacts, action, cancel, retry, and Operate event correlation endpoints under `/api/v1/admin/jobs/**` (#1170).
- [Share Export and Traffic API](internal/developer/share-export-traffic-api.md) — Scheduled Share export definitions, run history with nullable `jobRunId`, and aggregate Share traffic panels under `/api/v1/admin/share/**`, plus per-item traffic under `/api/v1/admin/services/{serviceName}/layers/{layerId}/share/**` (#1216).
- [Operate Observability Fixtures](internal/admin-api/operate-observability-fixtures.md) — Development/Test seed endpoint for Console Testcontainers to hydrate Operate events, logs, alerts, jobs, artifacts, and investigations from a real honua-server plus PostgreSQL runtime (#1209).
- [Metadata Prevalidation Admin API](internal/admin-api/metadata-prevalidation.md) — Metadata v2 release-package compatibility reports plus release operation lifecycle and rollback contract notes for Console (#1164/#1165).
- [Studio Package Lifecycle](internal/admin-api/studio-package-lifecycle.md) — Server-owned draft, validation, preview, immutable version, publish, reopen, compare, and rollback endpoints under `/api/v1/studio/**` (#1180).
- [Content Publication Registry](internal/admin-api/content-publication-registry.md) — Durable map/dashboard/report/generated-app publication records, active route pointers, public route resolution, share/embed/public-link policy, and rollback under `/api/v1/console/publications/**` and `/api/v1/published/**` (#1183).
- [Analysis Content](internal/admin-api/analysis-content.md) — Durable saved-query and analysis-package content items, immutable versions, preview artifacts, run/rerun provenance, artifact binding lookup, and failed-job diagnostics under `/api/v1/analysis/**` (#1182).
- [Scene Dataset Registry](internal/admin-api/scene-dataset-registry.md)

## Contributor Guides

For people writing code in this repo.

- [Contributor Guide (index)](internal/contributor/README.md)
- [Getting Started](internal/contributor/development/getting-started.md)
- [Contributing](internal/contributor/development/contributing.md)
- [k3d + Helm Local Dev](internal/contributor/development/k3d-helm.md)
- [LLM Review Setup](internal/contributor/development/llm-review-setup.md)
- [CI Quality Gates](internal/contributor/CI_QUALITY_GATES.md)
- [Release Checklist](internal/contributor/RELEASE_CHECKLIST.md)
- [Backlog Review Cadence](internal/contributor/BACKLOG_REVIEW_CADENCE.md)
- [Adaptive Sampling](internal/contributor/ADAPTIVE_SAMPLING.md)
- [Code Model Optimization](internal/contributor/CODE_MODEL_OPTIMIZATION.md)
- [GeoETL Roadmap](internal/contributor/geoetl-roadmap.md)
- [Testkit](internal/contributor/testkit.md)
- [Test Seed Data](internal/contributor/test-seed-data.md)
- [Testing — JavaScript](internal/contributor/testing-javascript.md)
- [Testing — Python](internal/contributor/testing-python.md)
- [Testing — MapLibre in Browser](internal/contributor/testing-maplibre-browser.md)
- [MCP Certification](internal/contributor/mcp-certification.md)
- [Compatibility and Migration Evidence](internal/contributor/compatibility-and-migration-evidence.md)
- [Process Migration Evidence](internal/contributor/process-migration-evidence.md)
- [Import Capability Evidence](internal/contributor/import-capability-evidence.md)

### CI

- [CI Gate Model](internal/ci/gate-model.md)
- [CI Config Conventions](internal/ci/config-conventions.md)
- [CI Workflow Inventory](internal/ci/workflow-inventory.md)

## GIS & Protocols

Protocol coverage, parity matrices, and how to consume Honua from desktop and analyst tools.

- [GIS Guide (index)](archive/role-indexes/gis-README.md)
- [Standards & APIs Overview](concepts/protocols.md)
- [Data Modeling Guide](concepts/data-model.md)
- [FileGDB Import Workflow](guides/publish/filegdb-import-workflow.md)
- [MVP Compatibility Contract](reference/compatibility/clients.md)
- [Cross-Client Certification Matrix](gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md)
- [Cross-Client Certification Evidence](gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
- [ArcGIS Pro Licensed Evidence](internal/evidence/ARCGIS_PRO_LICENSED_EVIDENCE.md)
- [Client Template Runbook](gis/CLIENT_TEMPLATE_RUNBOOK.md)
- [Client Template Version Matrix](gis/CLIENT_TEMPLATE_VERSION_MATRIX.md)
- [Client Templates Index](gis/client-templates/README.md)
- [Gap Report](gis/gap-report.md)
- [Feature Server Matrix](reference/compatibility/feature-server-matrix.md)
- [Map Server Matrix](reference/compatibility/map-server-matrix.md)
- [Image Server Matrix](reference/compatibility/image-server-matrix.md)
- [Geometry Service Matrix](reference/compatibility/geometry-service-matrix.md)
- [GeoServices REST Parity](reference/compatibility/geoservices-parity.md)
- [GeoProcess Framework Analysis](guides/query-analyze/run-geoprocessing.md)
- [I3S Compatibility Matrix](internal/spikes/i3s-compatibility-matrix.md)
- [Style Engine Protocol Consumption](guides/style/style-maps.md)
- [Visual Style Certification Slice](internal/evidence/visual-style-certification-slice.md)
- [Raster Overview](guides/publish/publish-rasters.md)
- [Cloud-Optimized HDF/NetCDF Support](guides/publish/cloud-optimized-hdf-netcdf-support.md)
- [Elevation API](reference/protocols/terrain-and-elevation.md)
- [Terrain Tiles](guides/publish/publish-terrain-and-elevation.md)
- [Temporal Animation API](guides/query-analyze/work-with-time.md)
- [Extruded 3D Feature Layers](guides/publish/extruded-3d-feature-layers.md)
- [Scene Generation](guides/publish/scene-generation.md)
- [Scenes / 3D Tiles](guides/publish/publish-3d-scenes.md)
- [Point Cloud / Reality Capture Ingest](internal/spikes/point-cloud-reality-capture-ingest.md)
- [OpenUSD / Omniverse Export Path](internal/spikes/openusd-omniverse-export-path.md)

### Specifications (per-protocol coverage)

- [Specifications Index](reference/protocols/specifications/README.md)
- [OData v4 Coverage](reference/protocols/specifications/odata-v4-coverage.md)
- [OGC API Features — Part 1 (Core)](reference/protocols/specifications/ogc-api-features-part1-core.md)
- [OGC API Features — Part 2 (CRS)](reference/protocols/specifications/ogc-api-features-part2-crs.md)
- [OGC API Features — Part 3 (Filtering)](reference/protocols/specifications/ogc-api-features-part3-filtering.md)
- [OGC API Features — Overall Coverage](reference/protocols/specifications/ogc-api-features-coverage.md)
- [OGC API Coverages](reference/protocols/specifications/ogc-api-coverages-coverage.md)
- [OGC API Processes](reference/protocols/specifications/ogc-api-processes-coverage.md)
- [OGC API Records](reference/protocols/specifications/ogc-api-records-coverage.md)
- [OGC API Tiles](reference/protocols/specifications/ogc-api-tiles-coverage.md)
- [WCS 2.0.1](reference/protocols/specifications/wcs-2.0.1-coverage.md)

### Tutorials

- [QGIS Getting Started](guides/connect/qgis.md)
- [GeoServer Migration Guide](guides/migrate/from-geoserver.md)

## Evidence & Audit

How we substantiate claims about conformance, compatibility, and performance.

- [Evidence Index](internal/evidence/README.md) — cross-cutting map of every evidence artifact.
- [Migration Performance Evidence](internal/evidence/migration-performance-evidence.md)
- [Cross-Server Consume Gap Report](internal/compatibility/cross-server-consume-gap-report.md)
- [OGC CITE Conformance Evidence](internal/contributor/ogc-cite-conformance-evidence.md)
- [CITE Status](cite-status.md)
- [Compatibility and Migration Evidence](internal/contributor/compatibility-and-migration-evidence.md)
- [Process Migration Evidence](internal/contributor/process-migration-evidence.md)
- [Import Capability Evidence](internal/contributor/import-capability-evidence.md)
- [Cross-Client Certification Evidence](gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
- [Certification Evidence (dated bundles)](gis/certification-evidence/20260402T000000Z/README.md)

## Features & Capabilities

- [Feature Map](internal/features/README.md) — source-backed map of every shipped capability.
- [Platform Overview](concepts/architecture.md)
- [Summary](SUMMARY.md)

## Demos

- [NVIDIA Construction Demo](internal/demo/nvidia-construction.md)

## API Specifications

- [Admin API](developer/api-specs/admin-api.json) — Server management (curated; use `/api/v1/admin/config` for full discovery)
- [OGC API Features](developer/api-specs/ogc-api-features.json) — Feature query and CRUD
- [OGC API Tiles](developer/api-specs/ogc-api-tiles.json) — Vector and raster tiles

## Archive

Historical planning, audit, and design artifacts that are not part of the current product contract live under [docs/archive/](archive/README.md). They are preserved for context but do not represent shipped behavior.
