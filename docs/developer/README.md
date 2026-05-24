# Developer Guide

Build applications and integrations with Honua APIs and SDKs.

## API Reference

- [API Examples](API_EXAMPLES.md) — Request/response examples for major Honua protocols
- [Integration Patterns](INTEGRATION_PATTERNS.md) — Common integration approaches with code samples
- [Metadata and Catalog Parity Matrix](metadata-catalog-parity-matrix.md) — Canonical server endpoint inventory and SDK parity contract for catalog and metadata reads
- [Capability Manifest](capability-manifest.md) — Neutral runtime capability discovery for Console, MCP, QGIS, native hosts, and SDK clients
- [Console Content and RBAC (Admin API)](../admin-api/console-content-and-rbac.md) — Honua Console metadata v2 session bootstrap, content CRUD/list/search, action-check, and provenance traversal under `/api/v1/console/**`
- [Console Job Observability (Admin API)](../admin-api/console-job-observability.md) — Durable job list/detail/log/artifact/action contract for Console job viewers under `/api/v1/admin/jobs/**`
- [Studio Package Lifecycle API](../admin-api/studio-package-lifecycle.md) — shared package draft, immutable version, publish, reopen, compare, rollback, and SDK projection contract under `/api/v1/studio/**`
- [Scene Dataset Registry (Admin API)](../admin-api/scene-dataset-registry.md) — Register, list, update, deactivate, and resolve hosted 3D scene datasets
- [NVIDIA Construction Demo Fixture](../demo/nvidia-construction.md) — Local-first 3D Tiles + observations sidecar fixture for the NVIDIA demo (no AWS, Azure, or Cesium ion)
- [Form Package API](form-package-api.md) — Versioned form package drafts, validation, immutable publishing, offline policy discovery, and field submission contracts
- [FieldCollection Mobile Sync API](fieldcollection-mobile-sync-api.md) — Generation, sync-cursor, pull, and push endpoints under `/api/v1/fieldcollection/` consumed by the `honua-mobile` offline sync clients
- [OpenAPI Specs](api-specs/) — Machine-readable API definitions
  - [Admin API](api-specs/admin-api.json) (curated subset; use `/api/v1/admin/config` for full discovery)
  - [OGC API Features](api-specs/ogc-api-features.json)
  - [OGC API Tiles](api-specs/ogc-api-tiles.json)
  - [OGC API Coverages](api-specs/ogc-api-coverages.json)

## SDKs

- [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md) — Server/SDK version support
- [SDK Standards Coverage](SDK_STANDARDS_COVERAGE.md) — Server-owned SDK coverage by language and protocol
- [SDK Metadata Format](SDK_COMPATIBILITY_METADATA.md) — Compatibility metadata schema
- [Mobile SDK Roadmap](mobile-sdk-roadmap.md) — Read / write / edit / sync / offline-cache cycle plan for `honua-mobile-sdk` (MAUI, iOS + Android)
- [MCP Server](MCP_SERVER.md) — SDK-hosted discovery/query MCP package plus the server-owned operator surface for AI agents
- [AI Builder SDK Contract](../ai-builder-sdk-contract.md) — Workflow, resource, fixture case, and capability-state reference that maps honua-server MCP surfaces to the honua-sdk-js AI Spatial App Builder sample
- [AI Builder Contract Fixtures](ai-builder-contract-fixtures.md) — Deterministic spatial-query and operations-dashboard app-builder fixtures for SDK, Portal, and MCP replay
- [Spec Plan/Apply Engine](SPEC_ENGINE.md) — Terraform-style plan/apply for canonical specs with content-hash artifact cache (REST + gRPC)
- [Grounding & Intent Drafting](GROUNDING.md) — Pipeline behind `honua_ground_candidates` / `honua_clarify_intent`: workflow-family classifier, candidate ranking, material-ambiguity rule set, and deterministic engine

## Spec Grammar

- [Spec Grammar v1.0](spec-grammar/v1.0/README.md) — Declarative geospatial spec language (source, scope, compute, map, output) + [JSON Schema](spec-grammar/v1.0/spec.schema.json) and [EBNF](spec-grammar/v1.0/spec.ebnf)
- [Spec Grounding v1.0](spec-grounding/v1.0/README.md) — Deterministic NL mutate/summarize endpoints for canonical specs, structured clarifications, and failure envelopes

## Internal Architecture

- [Redis Fallback Patterns](REDIS_FALLBACK_PATTERNS.md) — Standardized Redis health monitoring, circuit breaker, and fallback strategies
- [Configuration and secrets](../contributor/architecture/configuration-and-secrets.md) — secret-reference formats, validation attributes, standard TTL tiers

## Versioning & Migration

- [Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) — Deprecation lifecycle and breaking change rules
- [Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md) — Server and SDK upgrade procedures
