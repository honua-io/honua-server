# Developer Guide

Build applications and integrations with Honua APIs and SDKs.

## API Reference

- [API Examples](../../guides/query-analyze/query-features.md) — Request/response examples for major Honua protocols
- [Integration Patterns](../../reference/integration-patterns.md) — Common integration approaches with code samples
- [Metadata and Catalog Parity Matrix](../../internal/developer/metadata-catalog-parity-matrix.md) — Canonical server endpoint inventory and SDK parity contract for catalog and metadata reads
- [Capability Manifest](../../reference/admin-api/capability-manifest.md) — Neutral runtime capability discovery for Console, MCP, QGIS, native hosts, and SDK clients
- [Console Content, Share, and RBAC (Admin API)](../../internal/admin-api/console-content-and-rbac.md) — Honua Console metadata v2 session bootstrap, content CRUD/list/search, action-check, provenance traversal, Share access, public-link, and embed contracts under `/api/v1/console/**`
- [Console Workflow Packages (Admin API)](../../internal/admin-api/console-workflow-packages.md) — Server-owned GP/ETL node registry, workflow package versioning, validation, dry-run, publication, runs, and provenance under `/api/v1/console/workflow-*`
- [Console Job Observability (Admin API)](../../internal/admin-api/console-job-observability.md) — Durable job list/detail/log/artifact/action contract for Console job viewers under `/api/v1/admin/jobs/**`
- [Share Export and Traffic API](../../internal/developer/share-export-traffic-api.md) — Scheduled Share export definitions, run history with Operate job links, and aggregate Share traffic under `/api/v1/admin/share/**`, plus per-item traffic under `/api/v1/admin/services/{serviceName}/layers/{layerId}/share/**`
- [Operate Observability Fixtures (Admin API)](../../internal/admin-api/operate-observability-fixtures.md) — Development/Test seed profile for Console Testcontainers against a real server and PostgreSQL
- [Studio Package Lifecycle API](../../internal/admin-api/studio-package-lifecycle.md) — shared package draft, immutable version, publish, reopen, compare, rollback, and SDK projection contract under `/api/v1/studio/**`
- [Scene Dataset Registry (Admin API)](../../internal/admin-api/scene-dataset-registry.md) — Register, list, update, deactivate, and resolve hosted 3D scene datasets
- [NVIDIA Construction Demo Fixture](../../internal/demo/nvidia-construction.md) — Local-first 3D Tiles + observations sidecar fixture for the NVIDIA demo (no AWS, Azure, or Cesium ion)
- [Form Package API](../../reference/admin-api/forms.md) — Versioned form package drafts, validation, immutable publishing, offline policy discovery, and field submission contracts
- [FieldCollection Mobile Sync API](../../internal/developer/fieldcollection-mobile-sync-api.md) — Generation, sync-cursor, pull, and push endpoints under `/api/v1/fieldcollection/` consumed by the `honua-mobile` offline sync clients
- [Package Review API](../../internal/developer/package-review-api.md) — Shared validation and read-only preview planning contract for publish/execute candidates across HTTP, MCP, SDK, CI, and generated-app clients
- [OpenAPI Specs](../../developer/api-specs) — Machine-readable API definitions
  - [Admin API](../../developer/api-specs/admin-api.json) (curated subset; use `/api/v1/admin/config` for full discovery)
  - [OGC API Features](../../developer/api-specs/ogc-api-features.json)
  - [OGC API Tiles](../../developer/api-specs/ogc-api-tiles.json)
  - [OGC API Coverages](../../developer/api-specs/ogc-api-coverages.json)

## SDKs

- [SDK Compatibility Matrix](../../concepts/ecosystem.md) — Server/SDK version support
- [SDK Standards Coverage](../../internal/developer/SDK_STANDARDS_COVERAGE.md) — Server-owned SDK coverage by language and protocol
- [SDK Metadata Format](../../internal/developer/SDK_COMPATIBILITY_METADATA.md) — Compatibility metadata schema
- [Mobile SDK Roadmap](../../internal/developer/mobile-sdk-roadmap.md) — Read / write / edit / sync / offline-cache cycle plan for `honua-mobile-sdk` (MAUI, iOS + Android)
- [MCP Server](../../guides/connect/ai-agents-mcp.md) — SDK-hosted discovery/query MCP package plus the server-owned operator surface for AI agents
- [AI Builder SDK Contract](../../internal/developer/ai-builder-sdk-contract.md) — Workflow, resource, fixture case, and capability-state reference that maps honua-server MCP surfaces to the honua-sdk-js AI Spatial App Builder sample
- [AI Builder Contract Fixtures](../../internal/developer/ai-builder-contract-fixtures.md) — Deterministic spatial-query and operations-dashboard app-builder fixtures for SDK, Portal, and MCP replay
- [Spec Plan/Apply Engine](../../reference/spec-engine.md) — Terraform-style plan/apply for canonical specs with content-hash artifact cache (REST + gRPC)
- [Grounding & Intent Drafting](../../internal/developer/GROUNDING.md) — Pipeline behind `honua_ground_candidates` / `honua_clarify_intent`: workflow-family classifier, candidate ranking, material-ambiguity rule set, and deterministic engine

## Spec Grammar

- [Spec Grammar v1.0](../../developer/spec-grammar/v1.0/README.md) — Declarative geospatial spec language (source, scope, compute, map, output) + [JSON Schema](../../developer/spec-grammar/v1.0/spec.schema.json) and [EBNF](../../developer/spec-grammar/v1.0/spec.ebnf)
- [Spec Grounding v1.0](../../developer/spec-grounding/v1.0/README.md) — Deterministic NL mutate/summarize endpoints for canonical specs, structured clarifications, and failure envelopes

## Internal Architecture

- [Redis Fallback Patterns](../../internal/developer/REDIS_FALLBACK_PATTERNS.md) — Standardized Redis health monitoring, circuit breaker, and fallback strategies
- [Configuration and secrets](../../internal/contributor/architecture/configuration-and-secrets.md) — secret-reference formats, validation attributes, standard TTL tiers

## Versioning & Migration

- [Versioning Policy](../../reference/versioning-and-support.md) — Deprecation lifecycle and breaking change rules
- [Migration Guide](../../reference/control-plane-migration-guide.md) — Server and SDK upgrade procedures
