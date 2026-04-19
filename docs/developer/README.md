# Developer Guide

Build applications and integrations with Honua APIs and SDKs.

## API Reference

- [API Examples](API_EXAMPLES.md) — Request/response examples for major Honua protocols
- [Integration Patterns](INTEGRATION_PATTERNS.md) — Common integration approaches with code samples
- [OpenAPI Specs](api-specs/) — Machine-readable API definitions
  - [Admin API](api-specs/admin-api.json) (curated subset; use `/api/v1/admin/config` for full discovery)
  - [OGC API Features](api-specs/ogc-api-features.json)
  - [OGC API Tiles](api-specs/ogc-api-tiles.json)

## SDKs

- [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md) — Server/SDK version support
- [SDK Metadata Format](SDK_COMPATIBILITY_METADATA.md) — Compatibility metadata schema
- [MCP Server](MCP_SERVER.md) — SDK-hosted discovery/query MCP package plus the server-owned operator surface for AI agents

## Internal Architecture

- [Redis Fallback Patterns](REDIS_FALLBACK_PATTERNS.md) — Standardized Redis health monitoring, circuit breaker, and fallback strategies
- [Service Registration Consolidation](SERVICE_REGISTRATION_CONSOLIDATION.md) — Reusable service registration framework for feature slices

## Versioning & Migration

- [Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) — Deprecation lifecycle and breaking change rules
- [Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md) — Server and SDK upgrade procedures
