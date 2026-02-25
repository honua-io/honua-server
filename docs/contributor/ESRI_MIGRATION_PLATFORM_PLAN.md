# Esri Migration Platform Plan (JS-First, v0.3)

Last updated: February 25, 2026

## Planning Stance

Priority and sequencing matter more than schedule estimates for this effort.
This plan is organized around dependencies and phase gates, not fixed week counts.

## Non-Negotiable Decisions

1. Keep two lanes:
- `@honua/sdk` is the default Honua-first SDK.
- `@honua/sdk-esri-compat` is migration-only and opt-in.

2. Use one shared capability registry (Tier A/B/C) across importer, runtime, SDK, and migration CLI.

3. Treat migration as assisted conversion, not one-click conversion.

4. Do not build a second serving runtime.
- Imported services must flow through the existing Honua service registration and protocol endpoints.

5. CSM must extend existing Honua models instead of creating a disconnected parallel model.

## Architecture Overview

### Components

1. `honua-model-csm` (logical model + capability registry)
2. `honua-import-esri-service` (service metadata/data import into Honua)
3. `honua-import-publish-adapter` (normalizes import output into existing publish/catalog paths)
4. `@honua/sdk` (MapLibre-native)
5. `@honua/sdk-esri-compat` (optional migration facade)
6. `honua-migrate` (scanner + codemods + migration report)

### Runtime Clarification

`honua-runtime-publisher` from v0.1 is renamed to `honua-import-publish-adapter` to avoid ambiguity.
It is not a new runtime. It is an adapter into existing flows.

Import path should be:

1. ArcGIS service/file discovery/import
2. Transform to Honua publish inputs
3. Register using existing catalog/publishing paths
4. Serve through existing `/rest`, `/ogc`, `/odata`, `/mvt` endpoints

No alternate serving stack should be introduced.

## CSM Design (Detailed)

### Core Principle

CSM is an extension layer over existing domain models in:

- `Honua.Core.Features.Catalog.Domain` (`ServiceDefinition`, `LayerDefinition`, `FieldDefinition`, `CatalogMetadata`)
- `Honua.Core.Features.Styling.Domain` (`LayerStyleDefinition`)
- `Honua.Core.Features.Metadata.Domain` (versioned metadata resource envelope)

### Reuse vs Additions

| Concern | Existing Honua Model | CSM Action |
|---|---|---|
| Service identity/layers | `ServiceDefinition` | Reuse directly |
| Layer schema and fields | `LayerDefinition`, `FieldDefinition` | Reuse directly |
| Service/layer policy and protocol config | `CatalogMetadata` | Reuse, extend annotations |
| Renderer/style metadata | `LayerStyleDefinition` | Reuse for canonical + cached drawing info |
| Versioned model envelope | `MetadataResource` / compiled artifacts | Reuse for CSM resource storage |
| Migration capability tiers | None | Add |
| Fidelity diagnostics | None | Add |
| Auth migration mapping | None | Add |
| CRS migration profile | Partial | Add |

### CSM Entity Set (V1)

1. `CsmServiceProfile`
- Service name, description, default SR, extent, protocol exposure, import provenance.

2. `CsmLayerProfile`
- Layer id/name, geometry type, SR, extent, fields, relationships, attachment support.

3. `CsmFieldProfile`
- Name, type, nullability, length/domain/default/editability.

4. `CsmRendererProfile`
- Canonical renderer shape for simple/unique/class breaks subset.

5. `CsmPopupProfile`
- Title/content/field info/media subset for popup parity.

6. `CsmQueryCapabilityProfile`
- Supported filters, order/pagination semantics, outFields behavior, geometry query options.

7. `CsmEditCapabilityProfile`
- Create/update/delete and constraints.
- V1 pilot is read/query only. Edit capability profiles are captured during import for fidelity reporting but not exercised at runtime until post-pilot.

8. `CsmAuthProfile`
- Source auth requirement and migration mapping recommendation.

9. `CsmSpatialReferenceProfile`
- Source WKID/SRID, transformation status, runtime projection constraints.

10. `CsmFidelityReport`
- Tier A/B/C per operation/feature and reason codes.

### Storage/Compilation

1. Persist CSM extensions as versioned metadata resources and compiled artifacts.
2. Avoid introducing separate CSM catalog tables in V1.
3. Keep `ServiceDefinition`/`LayerDefinition` as runtime source for serving paths.

### CSM Versioning Policy

1. CSM schema versions follow semver (`CsmVersion` field on all persisted profiles).
2. Minor version bumps (e.g., v1.1) must be backward-compatible: new optional fields only, no removed or renamed fields.
3. Major version bumps (e.g., v2.0) may require re-import. Previously imported services retain their original schema version and continue to serve.
4. Runtime must support reading the current major version and one prior major version. Older imports surface a re-import advisory, not a hard failure.

## Data Migration Strategy

Metadata import and app migration are not sufficient without data movement into PostGIS.

### Scope

1. Definitions migration: service/layer/field/style/capability import.
2. Data migration: feature transfer into PostGIS tables.
3. App migration: code and SDK migration assistance.

### Source Coverage Plan

| Source Type | V1 Status | Notes |
|---|---|---|
| ArcGIS Feature/Map service endpoints | Supported | Uses existing geoservices import path |
| ArcGIS Online/Portal hosted layers exposed via REST | Supported with auth work | Requires token/OAuth support in importer |
| File-based sources (GeoJSON/Shapefile/etc.) | Supported | Uses existing file import pipeline |
| Enterprise geodatabase direct connectors | Deferred | Post-V1; migration workaround required |

### Pilot Dependency Rule

Every pilot must include:

1. a validated data path into PostGIS, and
2. automated reconciliation checks,

before app-migration signoff.

### Reconciliation Harness

Reconciliation is an automated validation step, not a manual spot-check. The harness runs after data import and produces a pass/fail report covering:

1. Feature count comparison (source service vs PostGIS table).
2. Geometry validity check (`ST_IsValid` on all imported geometries).
3. Key attribute sampling (configurable field list, null/type/cardinality checks).
4. Spatial extent comparison (source extent vs PostGIS extent within tolerance).

The harness is a deliverable in Epic F (Pilot Delivery) and reusable for all subsequent imports.

## Authentication and Identity Mapping

### Import-Time Auth

Importer must support authenticated ArcGIS discovery and query.

Planned additions:

1. Token/API-key/OAuth credential references in import requests.
2. Secret reference storage via existing secure connection/secret patterns.
3. Outbound request auth injection in importer HTTP client (not URL-embedded credentials).

### Client/Auth Migration

Produce a mapping matrix in migration report:

1. ArcGIS token model -> Honua auth mode recommendation (`API key`, `OIDC`, tenant policy).
2. Required claims/roles -> `CatalogMetadata.AccessPolicy` guidance.
3. Endpoint-level auth gaps flagged as manual tasks.

## Spatial Reference and Projection Strategy

### Required Capabilities

1. Preserve source WKID/SRID metadata in CSM.
2. Record ingest target SRID and transformation status.
3. Support runtime request `outSR` transformations where available.
4. Expose explicit warnings for unsupported/custom CRS scenarios.

### Rendering Strategy

1. Store canonical geometry in PostGIS with explicit SRID.
2. Default web rendering path targets MapLibre-friendly projection behavior.
3. Avoid silent reprojection failures; emit diagnostics in import and migration reports.

## SDK Strategy and Compat Lifecycle

### SDK Roles

1. `@honua/sdk`
- Primary long-term API for Honua-first and migrated apps after stabilization.

2. `@honua/sdk-esri-compat`
- Bridge for migration acceleration; not a permanent feature-growth surface.

### Compat Lifecycle Policy

Reality: many ArcGIS users will migrate slowly or run dual-stack for extended periods. The compat SDK must be a durable, low-cost surface — not a ticking clock that pressures customers before they are ready.

1. Compat package is a supported, long-term product surface for migration scenarios.
2. New capability work lands in `@honua/sdk` first. Compat receives wrappers only for migration-critical patterns.
3. Compat API surface is frozen after initial migration coverage is complete — no feature growth, only bug fixes and compatibility maintenance.
4. Dev-mode hints (not warnings) should surface native SDK equivalents to encourage organic migration without creating upgrade pressure.
5. Transition to maintenance mode (bug-fix only, no new wrappers) is criteria-based:
   - native SDK covers the top migration patterns,
   - migration cookbook documents all compat-to-native mappings,
   - no net-new compat wrappers requested for two consecutive release cycles.
6. Deprecation is not planned for V1 horizon. If deprecation is ever considered, it requires 18-month notice and a demonstrated native replacement path for every compat API in active use.

## Phase Sequence (Gate-Based)

### Phase 0: Preconditions

1. Obtain representative service/app fixtures (with legal/security approvals).
2. Define migration target personas and top workflows.
3. Lock pilot candidate(s) and data access path(s).

Gate to Phase 1:
- fixture set is available and representative enough to shape CSM.

### Phase 1: Foundation

1. Implement CSM extensions over existing domain model.
2. Implement capability registry (Tier A/B/C + reason codes).
3. Implement import-publish adapter into existing catalog/publish flow.
4. Add importer auth support and CRS diagnostics.
5. Add import pipeline observability (Epic J).

Gate to Phase 2:
- imported services are served through existing Honua endpoints with deterministic fidelity report.

### Phase 2: JS Migration Delivery

1. Deliver `@honua/sdk` core subset.
2. Deliver scoped `@honua/sdk-esri-compat`.
3. Deliver `honua-migrate` scanner + safe codemods + report.

Gate to Phase 3:
- one real app reaches first-map + first-query parity with bounded manual tasks.

### Phase 3: Pilot Hardening

1. Validate pilot end-to-end (data + service + app migration).
2. Close high-priority parity and reliability gaps.
3. Publish migration cookbook and operator runbook.

Gate to Phase 4:
- pilot acceptance criteria met and support playbook ready.

### Phase 4: Expansion

1. Python SDK migration support (task-oriented client).
2. Mobile migration support (Honua-native wrappers + cookbook).
3. Continue compat-to-native transition guidance.

## Epic-to-Phase Mapping

| Epic | Description | Phase |
|---|---|---|
| A | CSM extensions + capability registry | Phase 1 |
| B | Esri service import + import-publish adapter | Phase 1 |
| C | JS core SDK (`@honua/sdk`) | Phase 2 |
| D | JS compat SDK (`@honua/sdk-esri-compat`) | Phase 2 |
| E | App migration CLI (`honua-migrate`) | Phase 2 |
| F | Pilot migration + reconciliation harness + parity validation | Phase 3 |
| G1 | Importer auth (token/OAuth/secret refs, outbound injection) | Phase 1 |
| G2 | Client auth mapping matrix in migration report | Phase 2 |
| G3 | Auth gap resolution and pilot auth validation | Phase 3 |
| H1 | CRS profile capture and import-time diagnostics | Phase 1 |
| H2 | SDK projection handling and unsupported-SR warnings | Phase 2 |
| H3 | Pilot CRS validation and edge-case resolution | Phase 3 |
| I | Python/mobile follow-on | Phase 4 |
| J | Import pipeline observability (structured logs, run IDs, report persistence) | Phase 1 |

## Success Metrics (Defined)

1. Manual rewrite ratio:
- Numerator: ArcGIS API call sites flagged as manual after codemods.
- Denominator: Total ArcGIS API call sites discovered by scanner.
- Target: <= 25% for pilot app(s).

2. Capability coverage:
- >= 80% of pilot-usage constructs resolved by Tier A/B.

3. Time-to-first-value:
- First migrated map + successful query within one focused implementation day.

4. Data correctness:
- No critical mismatches in feature count/geometry validity/key attribute checks on pilot datasets.

## Relationship to Current Server Work

Current protocol parity expansion work remains a dependency boundary.

### Prerequisite Stability Areas

1. GeoServices/OGC/OData endpoint behavior for migration-critical operations.
2. Query/filter and style behavior used by compat SDK subset.
3. Admin publish/catalog flows used by import-publish adapter.

### Parallelizable Now

1. CSM modeling and capability registry design.
2. Fixture collection and migration scanner prototypes.
3. Importer auth/CRS diagnostics design.

### Should Not Start Until Parity Behavior Stabilizes

1. Wide compat wrapper expansion.
2. Migration parity claims in external docs.
3. Hard migration KPI commitments.

### Focus Control

Migration work should consume a fixed capacity slice so protocol parity completion is not derailed.

## Import Pipeline Observability

When an import fails or produces unexpected fidelity results, operators need to debug without guessing.

### Requirements

1. Every import run gets a unique `ImportRunId` (ULID or UUID) that appears in all logs, reports, and persisted artifacts.
2. Structured logging for import stages: discovery, metadata parse, data transfer, publish, reconciliation. Each stage emits start/complete/fail events with the run ID.
3. Fidelity reports and reconciliation results are persisted and queryable by run ID.
4. Failed imports retain partial state and diagnostics for post-mortem rather than silently cleaning up.

### Deliverable

Epic J in Phase 1. This is operational infrastructure — it must ship before pilot imports begin, not after.

## Risk Register and Controls

1. CSM drift from runtime model.
- Control: CSM extends existing domain model; contract tests are merge gate.

2. Duplicate serving logic.
- Control: enforce import-publish adapter pattern only; no second runtime.

3. Data migration under-scoped.
- Control: pilot cannot pass without validated data transfer + reconciliation checks.

4. Auth complexity late in cycle.
- Control: treat importer auth as Phase 1 requirement, not backlog tail.

5. CRS incompatibility surprises.
- Control: explicit CRS profile + diagnostics + fail-fast rules for unsupported SRs.

6. Compat SDK scope creep.
- Control: API surface freeze after initial coverage; native-first investment; maintenance-mode gate criteria; no feature growth on compat surface.

## Feedback Questions

1. Should enterprise geodatabase direct connectors remain deferred, or be promoted earlier?
2. Is the CSM extension strategy over existing domain models acceptable?
3. Is the compat API-surface-freeze + maintenance-mode gate the right long-term posture, or should the compat surface accept limited growth indefinitely?
4. Which two customer apps should be fixture baselines for Phase 0?
5. Should edit capabilities be promoted into the V1 pilot, or is read/query sufficient for first customer acceptance?
