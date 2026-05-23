# Honua Server Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**

**New here?** Start with the [Platform Overview](PLATFORM.md) for architecture, protocols, and capabilities.
**Need to defend a claim?** The [Evidence Index](evidence/README.md) is the cross-cutting map of compatibility, conformance, parity, certification, and migration evidence across the repo.
Historical planning, audit, and design artifacts live under [docs/archive/](archive/README.md) and are not part of the current product contract.

This page is the canonical table of contents for every important doc in the repo. If you can't find something here, it is either archived under [docs/archive/](archive/README.md) or it does not exist yet — open an issue.

## By Role

| I am a... | Start here |
|---|---|
| **Server Operator** | [Operator Guide](operator/README.md) — deploy, configure, monitor, manage |
| **GIS Professional** | [GIS User Guide](gis/README.md) — connect desktop apps, consume services |
| **Developer** | [Developer Guide](developer/README.md) — APIs, SDKs, integrations |
| **Contributor** | [Contributor Guide](contributor/README.md) — architecture, testing, PRs |

## Quick Links

| I want to... | Go to |
|---|---|
| Deploy the server | [Infrastructure](operator/infrastructure.md) / [Docker Compose](operator/docker-compose.md) |
| Connect QGIS | [QGIS Tutorial](gis/tutorials/qgis-getting-started.md) |
| Connect ArcGIS Pro | [Client Templates](gis/CLIENT_TEMPLATE_RUNBOOK.md) |
| Manage services via API | [Control Plane API](operator/CONTROL_PLANE_API.md) |
| See API examples | [API Examples](developer/API_EXAMPLES.md) |
| Check protocol support | [Protocols Overview](gis/STANDARDS_APIS.md) |
| Serve terrain/elevation tiles | [Terrain-RGB Tiles](gis/terrain-tiles.md) |
| Look up numeric elevation values | [Elevation Query and Profile API](gis/elevation-api.md) |
| Integrate AI agents | [MCP Server](developer/MCP_SERVER.md) |
| Troubleshoot issues | [Troubleshooting](operator/troubleshooting.md) |
| Review OpenAPI specs | [API Specs](developer/api-specs/) |

## Standards & Compliance

The authoritative claims about what Honua conforms to, and the evidence behind them.

- [API Standards Summary](api-standards-summary.md) — consolidated map of OGC CITE pass rates, gRPC versioning policy, OpenAPI drift workflow, and API versioning strategy.
- [CITE Status](cite-status.md) — authoritative snapshot of OGC CITE pass rates per protocol on `trunk` (currently **952 / 952** across 11 suites).
- [OGC CITE Conformance Evidence](contributor/ogc-cite-conformance-evidence.md) — canonical, website-linkable summary with per-suite totals and evidence links.
- [Standards & APIs Overview](gis/STANDARDS_APIS.md) — every standard and protocol Honua speaks, with endpoint, version, and coverage links.
- [OGC Certification Path](contributor/ogc-certification-path.md) — what we have certified, what is in flight, and what is next.
- [CITE Runbook](contributor/cite-runbook.md) — how to run CITE locally and regenerate evidence bundles.
- [gRPC Versioning Policy](grpc-versioning-policy.md) — how the `Geospatial.V1` gRPC surface is versioned and what stability guarantees we make.
- [Control Plane Versioning Policy](developer/CONTROL_PLANE_VERSIONING_POLICY.md) — versioning and stability guarantees for the admin / control-plane API.
- [Control Plane Migration Guide](developer/CONTROL_PLANE_MIGRATION_GUIDE.md) — how to migrate clients across control-plane API versions.
- [MVP Compatibility Contract](gis/MVP_COMPATIBILITY_CONTRACT.md) — the supported client × protocol matrix for MVP.
- [Public Interface Quality Model](contributor/public-interface-quality-model.md) — how we score and gate public surfaces.
- [Package and Module Governance](contributor/package-and-module-governance.md) — how repo packages and modules are kept stable.

## Operations & Security

How to deploy, run, harden, and respond to incidents.

### Operator handbook

- [Operator Guide (index)](operator/README.md)
- [Infrastructure](operator/infrastructure.md) — supported runtimes, sizing, IaC notes.
- [Docker Compose](operator/docker-compose.md) — local + production-like compose stacks.
- [Operations](operator/operations.md) — day-2 operational tasks.
- [Monitoring](operator/monitoring.md) — metrics, traces, and SLO surfaces.
- [Troubleshooting](operator/troubleshooting.md)
- [Security](operator/security.md) — operator-facing hardening guidance.
- [TLS Connection Guide](operator/tls-connection-guide.md)
- [HTTP Client Resilience](operator/http-client-resilience.md)
- [Tile Operations Runbook](operator/tile-operations-runbook.md)
- [Control Plane API](operator/CONTROL_PLANE_API.md)
- [Deployment Scenarios](operator/DEPLOYMENT_SCENARIOS.md)
- [Database Support Matrix](operator/database-support-matrix.md)
- [Migration Pilot Cutover Checklist](operator/migration-pilot-cutover-checklist.md)
- [Migration Toolkit](operator/migration-toolkit.md)
- [SLD Migration](operator/sld-migration.md)
- [OGC API Features Migration](operator/ogc-api-features-migration.md)
- [OGC Coverage Migration](operator/ogc-coverage-migration.md)
- [PMTiles Publishing](operator/pmtiles-publishing.md)
- [Feature Streaming](operator/feature-streaming.md)
- [Feature Change Webhooks](operator/feature-change-webhooks.md)
- [ArcGIS Inventory Discovery](operator/arcgis-inventory-discovery.md)

### Provider guides

- [DuckDB Provider](operator/duckdb-provider.md)
- [MySQL / MariaDB Provider](operator/mysql-provider.md)
- [SQL Server Provider](operator/sqlserver-provider.md)

### Runbooks

- [Operator Runbooks (index)](operator/runbooks/README.md)
- [License Key Rotation](operator/runbooks/LICENSE_KEY_ROTATION.md)
- [License Migration](operator/runbooks/LICENSE_MIGRATION.md)
- [Marketplace Operations](operator/runbooks/MARKETPLACE_OPERATIONS.md)
- [Upgrade and Rollback](operator/runbooks/UPGRADE_AND_ROLLBACK.md)

### Security

- [Security Policy](../SECURITY.md) — supported versions, vulnerability reporting, disclosure process. **(repo root)**
- [Compliance Framework](operator/compliance-framework.md) — SOC 2 / FedRAMP readiness evidence collection, data residency policy + dry-run, compliance key-version rotation events, and report export.
- [Base URL & Open Redirect Handling](security/base-url-and-open-redirect-handling.md)
- [Code Scanning — 2026 Q2 Remediation](security/code-scanning-2026-Q2-remediation.md)
- [Security-First File Upload Design (ADR 0019)](contributor/adr/0019-security-first-file-upload-design.md)

## Architecture & Design

Strategic direction, the canonical architecture, and the decisions behind it.

- [AGENTS.md](../AGENTS.md) — repo-wide guide for human and AI agents working on the codebase. **(repo root)**
- [Platform Overview](PLATFORM.md) — what Honua is and how the pieces fit together.
- [Architecture (contributor)](contributor/ARCHITECTURE.md)
- [Architecture Diagrams](contributor/ARCHITECTURE_DIAGRAMS.md)
- [Architecture Criteria](contributor/architecture-criteria.md)
- [Honua Manifesto](contributor/HONUA_MANIFESTO.md)

### Architecture notes

- [Admin Operator Workflows](contributor/architecture/admin-operator-workflows.md)
- [Configuration and Secrets](contributor/architecture/configuration-and-secrets.md)
- [Metadata v2 — Admin UI Information Model](contributor/architecture/metadata-v2-admin-ui-information-model.md)
- [Metadata v2 — Release Readiness](contributor/architecture/metadata-v2-release-readiness.md)
- [Unified License and Entitlement](contributor/architecture/unified-license-and-entitlement.md)

### Architecture Decision Records

- [ADR Index](contributor/adr/README.md) — every accepted ADR, in numeric order. Recent additions:
  - [ADR 0036 — Mobile SDK Language Strategy](contributor/adr/0036-mobile-sdk-language-strategy.md)
  - [ADR 0037 — Unified CI Test Tier Strategy](contributor/adr/0037-unified-ci-test-tier-strategy.md)
  - [ADR 0038 — GeoETL Pipeline Architecture and Runtime Boundary](contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
  - [ADR 0039 — Cloud-Optimized HDF/NetCDF Reader Strategy](contributor/adr/0039-cloud-optimized-hdf-netcdf-reader-strategy.md)

## Developer Guides

How to build against Honua's APIs, SDKs, and protocols.

- [Developer Guide (index)](developer/README.md)
- [API Examples](developer/API_EXAMPLES.md)
- [Integration Patterns](developer/INTEGRATION_PATTERNS.md)
- [Grounding](developer/GROUNDING.md) — natural-language grounding for AI builders.
- [MCP Server](developer/MCP_SERVER.md) — JSON-RPC surface for AI agents.
- [Redis Fallback Patterns](developer/REDIS_FALLBACK_PATTERNS.md)
- [Spec Engine](developer/SPEC_ENGINE.md)
- [AI Builder SDK Contract](ai-builder-sdk-contract.md) — Map of honua-server MCP surfaces to the honua-sdk-js AI Spatial App Builder workflow, fixture cases, and capability states.
- [AI Builder Contract Fixtures](developer/ai-builder-contract-fixtures.md)
- [SDK Compatibility Matrix](developer/SDK_COMPATIBILITY_MATRIX.md)
- [SDK Compatibility Metadata](developer/SDK_COMPATIBILITY_METADATA.md)
- [SDK Standards Coverage](developer/SDK_STANDARDS_COVERAGE.md)
- [SDK Migration Template](developer/sdk-migration-template.md)
- [Mobile SDK Roadmap](developer/mobile-sdk-roadmap.md)
- [FieldCollection Mobile Sync API](developer/fieldcollection-mobile-sync-api.md)
- [Mobile Offline Demo Fixture](developer/mobile-offline-demo-fixture.md)
- [Metadata Catalog Parity Matrix](developer/metadata-catalog-parity-matrix.md)
- [Spec Grammar v1.0](developer/spec-grammar/v1.0/README.md)
- [Spec Grounding v1.0](developer/spec-grounding/v1.0/README.md)
- [API Specs (OpenAPI bundles)](developer/api-specs/README.md)

### Admin API

- [Scene Dataset Registry](admin-api/scene-dataset-registry.md)

## Contributor Guides

For people writing code in this repo.

- [Contributor Guide (index)](contributor/README.md)
- [Getting Started](contributor/development/getting-started.md)
- [Contributing](contributor/development/contributing.md)
- [k3d + Helm Local Dev](contributor/development/k3d-helm.md)
- [LLM Review Setup](contributor/development/llm-review-setup.md)
- [CI Quality Gates](contributor/CI_QUALITY_GATES.md)
- [Release Checklist](contributor/RELEASE_CHECKLIST.md)
- [Backlog Review Cadence](contributor/BACKLOG_REVIEW_CADENCE.md)
- [Adaptive Sampling](contributor/ADAPTIVE_SAMPLING.md)
- [Code Model Optimization](contributor/CODE_MODEL_OPTIMIZATION.md)
- [GeoETL Roadmap](contributor/geoetl-roadmap.md)
- [Testkit](contributor/testkit.md)
- [Test Seed Data](contributor/test-seed-data.md)
- [Testing — JavaScript](contributor/testing-javascript.md)
- [Testing — Python](contributor/testing-python.md)
- [Testing — MapLibre in Browser](contributor/testing-maplibre-browser.md)
- [MCP Certification](contributor/mcp-certification.md)
- [Compatibility and Migration Evidence](contributor/compatibility-and-migration-evidence.md)
- [Process Migration Evidence](contributor/process-migration-evidence.md)
- [Import Capability Evidence](contributor/import-capability-evidence.md)

### CI

- [CI Gate Model](ci/gate-model.md)
- [CI Config Conventions](ci/config-conventions.md)
- [CI Workflow Inventory](ci/workflow-inventory.md)

## GIS & Protocols

Protocol coverage, parity matrices, and how to consume Honua from desktop and analyst tools.

- [GIS Guide (index)](gis/README.md)
- [Standards & APIs Overview](gis/STANDARDS_APIS.md)
- [Data Modeling Guide](gis/DATA_MODELING_GUIDE.md)
- [FileGDB Import Workflow](gis/FILEGDB_IMPORT_WORKFLOW.md)
- [MVP Compatibility Contract](gis/MVP_COMPATIBILITY_CONTRACT.md)
- [Cross-Client Certification Matrix](gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md)
- [Cross-Client Certification Evidence](gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
- [ArcGIS Pro Licensed Evidence](gis/ARCGIS_PRO_LICENSED_EVIDENCE.md)
- [Client Template Runbook](gis/CLIENT_TEMPLATE_RUNBOOK.md)
- [Client Template Version Matrix](gis/CLIENT_TEMPLATE_VERSION_MATRIX.md)
- [Client Templates Index](gis/client-templates/README.md)
- [Gap Report](gis/gap-report.md)
- [Feature Server Matrix](gis/feature-server-matrix.md)
- [Map Server Matrix](gis/map-server-matrix.md)
- [Image Server Matrix](gis/image-server-matrix.md)
- [Geometry Service Matrix](gis/geometry-service-matrix.md)
- [GeoServices REST Parity](gis/geoservices-rest-parity.md)
- [GeoProcess Framework Analysis](gis/geoprocess-framework-analysis.md)
- [I3S Compatibility Matrix](gis/i3s-compatibility-matrix.md)
- [Style Engine Protocol Consumption](gis/style-engine-protocol-consumption.md)
- [Visual Style Certification Slice](gis/visual-style-certification-slice.md)
- [Raster Overview](gis/raster-overview.md)
- [Cloud-Optimized HDF/NetCDF Support](gis/cloud-optimized-hdf-netcdf-support.md)
- [Elevation API](gis/elevation-api.md)
- [Terrain Tiles](gis/terrain-tiles.md)
- [Temporal Animation API](gis/temporal-animation-api.md)
- [Extruded 3D Feature Layers](gis/extruded-3d-feature-layers.md)
- [Scene Generation](gis/scene-generation.md)
- [Scenes / 3D Tiles](gis/scenes-3dtiles.md)
- [Point Cloud / Reality Capture Ingest](gis/point-cloud-reality-capture-ingest.md)
- [OpenUSD / Omniverse Export Path](gis/openusd-omniverse-export-path.md)

### Specifications (per-protocol coverage)

- [Specifications Index](gis/specifications/README.md)
- [OData v4 Coverage](gis/specifications/odata-v4-coverage.md)
- [OGC API Features — Part 1 (Core)](gis/specifications/ogc-api-features-part1-core.md)
- [OGC API Features — Part 2 (CRS)](gis/specifications/ogc-api-features-part2-crs.md)
- [OGC API Features — Part 3 (Filtering)](gis/specifications/ogc-api-features-part3-filtering.md)
- [OGC API Features — Overall Coverage](gis/specifications/ogc-api-features-coverage.md)
- [OGC API Coverages](gis/specifications/ogc-api-coverages-coverage.md)
- [OGC API Processes](gis/specifications/ogc-api-processes-coverage.md)
- [OGC API Records](gis/specifications/ogc-api-records-coverage.md)
- [OGC API Tiles](gis/specifications/ogc-api-tiles-coverage.md)
- [WCS 2.0.1](gis/specifications/wcs-2.0.1-coverage.md)

### Tutorials

- [QGIS Getting Started](gis/tutorials/qgis-getting-started.md)
- [GeoServer Migration Guide](gis/tutorials/geoserver-migration-guide.md)

## Evidence & Audit

How we substantiate claims about conformance, compatibility, and performance.

- [Evidence Index](evidence/README.md) — cross-cutting map of every evidence artifact.
- [Migration Performance Evidence](evidence/migration-performance-evidence.md)
- [Cross-Server Consume Gap Report](compatibility/cross-server-consume-gap-report.md)
- [OGC CITE Conformance Evidence](contributor/ogc-cite-conformance-evidence.md)
- [CITE Status](cite-status.md)
- [Compatibility and Migration Evidence](contributor/compatibility-and-migration-evidence.md)
- [Process Migration Evidence](contributor/process-migration-evidence.md)
- [Import Capability Evidence](contributor/import-capability-evidence.md)
- [Cross-Client Certification Evidence](gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
- [Certification Evidence (dated bundles)](gis/certification-evidence/20260402T000000Z/README.md)

## Features & Capabilities

- [Feature Map](features/README.md) — source-backed map of every shipped capability.
- [Platform Overview](PLATFORM.md)
- [Summary](SUMMARY.md)

## Demos

- [NVIDIA Construction Demo](demo/nvidia-construction.md)

## API Specifications

- [Admin API](developer/api-specs/admin-api.json) — Server management (curated; use `/api/v1/admin/config` for full discovery)
- [OGC API Features](developer/api-specs/ogc-api-features.json) — Feature query and CRUD
- [OGC API Tiles](developer/api-specs/ogc-api-tiles.json) — Vector and raster tiles

## Archive

Historical planning, audit, and design artifacts that are not part of the current product contract live under [docs/archive/](archive/README.md). They are preserved for context but do not represent shipped behavior.
