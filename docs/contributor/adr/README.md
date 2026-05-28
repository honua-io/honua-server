# Architecture Decision Records

This folder contains Architecture Decision Records (ADRs) for the Honua greenfield MVP.

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [0001](0001-raw-npgsql-no-orm.md) | Raw Npgsql over ORM/Dapper | Accepted | 2025-12 |
| [0002](0002-maplibre-canonical-style.md) | MapLibre as Canonical Style Format | Accepted | 2025-12 |
| [0003](0003-odata-full-crud.md) | OData v4 Full CRUD in MVP | Accepted | 2025-12 |
| [0004](0004-proxy-rate-limiting.md) | Proxy-Based Rate Limiting | Accepted | 2025-12 |
| [0005](0005-dbup-migrations.md) | DbUp for Database Migrations | Accepted | 2025-12 |
| [0006](0006-openfreemap-default-basemap.md) | OpenFreeMap as Default Basemap | Accepted | 2025-12 |
| [0007](0007-embedded-maputnik.md) | Embedded Maputnik Style Editor | Accepted; implementation incomplete | 2025-12 |
| [0008](0008-env-var-configuration.md) | Environment Variables as Primary Config | Accepted | 2025-12 |
| [0009](0009-shared-filter-ast.md) | Shared Filter AST for Multi-Protocol Support | Accepted | 2025-12 |
| [0010](0010-admin-ui-architecture.md) | Admin UI Architecture (Blazor WASM) | Accepted | 2025-12 |
| [0011](0011-testing-strategy.md) | Testing Strategy and API Surface Coverage | Accepted | 2025-12 |
| [0012](0012-clean-architecture-implementation.md) | Clean Architecture Implementation | Accepted | 2025-12 |
| [0013](0013-minimal-apis-vs-controllers.md) | Minimal APIs vs Controllers Decision | Accepted | 2025-12 |
| [0014](0014-dependency-injection-limits.md) | Dependency Injection Limits Rationale | Accepted | 2025-12 |
| [0015](0015-vertical-slice-architecture.md) | Vertical Slice Architecture Pattern | Accepted | 2025-12 |
| [0016](0016-performance-optimization-strategies.md) | Performance Optimization Strategies | Accepted | 2025-12 |
| [0017](0017-redis-caching-with-fallback.md) | Redis Caching with Fallback Strategy | Accepted | 2025-12 |
| [0018](0018-source-generated-json-serialization.md) | Source-Generated JSON Serialization for AOT Compatibility | Accepted | 2025-12 |
| [0019](0019-security-first-file-upload-design.md) | Security-First File Upload Design | Accepted | 2025-12 |
| [0020](0020-mvp-operational-deferrals.md) | MVP Operational Deferrals | Accepted | 2025-12 |
| [0021](0021-redis-usage-and-hybridcache-deferral.md) | Redis Usage and HybridCache Deferral | Accepted | 2025-12 |
| [0022](0022-no-transform-on-write.md) | No Transform on Write (Except Imports) | Accepted | 2025-12 |
| [0023](0023-metadata-architecture.md) | Metadata Resource Model and GitOps-Ready Storage | Accepted | 2025-12 |
| [0024](0024-open-core-edition-model.md) | Open-Core Edition Model | Accepted | 2026-03 |
| [0025](0025-multi-provider-operation-architecture.md) | Multi-Provider Operation Architecture | Accepted | 2026-03 |
| [0026](0026-ai-first-operator-contract.md) | AI-First Operator Contract as Primary Public Contract | Proposed | 2026-04 |
| [0027](0027-deterministic-intent-clarification-workflow.md) | Deterministic Intent, Clarification, and Plan Validation Workflow | Proposed | 2026-04 |
| [0028](0028-ai-data-editing-not-allowed.md) | AI-Driven Data Editing Is Not Allowed | Accepted | 2026-04 |
| [0029](0029-geoprocess-canonical-model-mappings.md) | Geoprocess Canonical Model Mappings | Accepted | 2026-04 |
| 0030 | _(reserved / withdrawn — number intentionally unused)_ | — | — |
| [0031](0031-durable-job-orchestration-substrate.md) | Durable Job Orchestration Substrate | Accepted | 2026-04 |
| [0032](0032-workflow-orchestration-layer.md) | Workflow Orchestration Layer | Accepted | 2026-04 |
| [0033](0033-unified-license-format.md) | Unified License Format and Entitlement Architecture | Accepted | 2026-04 |
| [0034](0034-gdal-honua-driver-delivery-strategy.md) | GDAL/OGR honua Driver Delivery Strategy | Accepted | 2026-04 |
| [0035](0035-provider-ready-data-source-binding.md) | Provider-Ready Data Source Binding | Accepted | 2026-04 |
| [0036](0036-mobile-sdk-language-strategy.md) | Mobile SDK Language Strategy | Accepted | 2026-04 |
| [0037](0037-unified-ci-test-tier-strategy.md) | Unified CI Test Tier Strategy | Accepted | 2026-04 |
| [0038](0038-geoetl-pipeline-architecture-and-runtime-boundary.md) | GeoETL Pipeline Architecture and Runtime Boundary | Accepted | 2026-05 |
| [0039](0039-cloud-optimized-hdf-netcdf-reader-strategy.md) | Cloud-Optimized HDF5 / NetCDF4 Reader Strategy | Accepted | 2026-05 |
| [0040](0040-metadata-v2-canonical-graph.md) | Metadata v2 Canonical Graph Design | Accepted (cutover in progress) | 2026-05 |
| [0041](0041-core-abstractions-extraction.md) | Honua.Core.Abstractions Extraction (Modularization Phase 0+1) | Accepted | 2026-05 |
| [0042](0042-per-protocol-test-project-split.md) | Per-Protocol Test Project Split (Modularization Phase 2) | Proposed | 2026-05 |
| [0043](0043-modularization-ci-rework.md) | Modularization CI Rework (Modularization Phase 3) | Proposed | 2026-05 |
| [0044](0044-server-infrastructure-decomposition.md) | Server.Features.Infrastructure Decomposition (Audit-A1 / Phase 1 Prerequisite) | Proposed | 2026-05 |
| [0045](0045-defer-migration-sequence-collision-renumbering.md) | Defer Renumbering of Colliding Migration Sequence Numbers | Accepted | 2026-05 |

## Template

```markdown
# ADR-NNNN: Title

## Status
Proposed | Accepted | Deprecated | Superseded

## Context
What is the issue that we're seeing that is motivating this decision?

## Decision
What is the change that we're proposing?

## Consequences
What becomes easier or more difficult because of this change?
```
