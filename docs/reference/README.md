# Reference

Lookup material for Honua's public surfaces: protocols, the admin API, configuration, compatibility status, and cross-cutting contracts. Task-oriented walkthroughs live in [guides](../guides/README.md).

## Sections

| Section | Contents |
| --- | --- |
| [Protocols](protocols/ogc-apis.md) | Per-protocol references: OGC APIs, classic OGC (WMS/WFS/WCS/WMTS), GeoServices REST, OData, STAC, vector tiles, terrain, 3D Tiles/scenes, gRPC. |
| [Admin API](admin-api/overview.md) | Control-plane usage: connections and layers, imports and jobs, styles, forms, users/roles/licensing. |
| [Configuration](configuration/environment-variables.md) | The canonical [environment variable reference](configuration/environment-variables.md) and [data source providers](configuration/data-sources/README.md). |
| [Compatibility](compatibility/ogc-conformance.md) | [OGC conformance](compatibility/ogc-conformance.md), [GeoServices parity](compatibility/geoservices-parity.md), [client compatibility](compatibility/clients.md). |

## Cross-cutting pages

| Page | One-liner |
| --- | --- |
| [Data formats](data-formats.md) | Import and export format matrix per surface, with size limits. |
| [CQL2 and filtering](cql2-and-filtering.md) | CQL2 text/JSON, GeoServices `where`, and OData `$filter`, side by side. |
| [Geoprocessing operations](geoprocessing-operations.md) | Catalog of built-in processes by family with parameters. |
| [Spec engine](spec-engine.md) | Plan/apply engine: endpoints, cache modes, diagnostics. |
| [OpenAPI and the explorer](openapi-and-explorer.md) | Runtime OpenAPI endpoints, `/docs`, and the pinned spec bundles. |
| [Versioning and support](versioning-and-support.md) | Admin API versioning, deprecation lifecycle, protocol and gRPC stability. |
| [Integration patterns](integration-patterns.md) | Capability discovery, pagination, polling vs webhooks, auth, idempotent loads. |
| [Control plane migration guide](control-plane-migration-guide.md) | SDK generation and breaking-change upgrade flow for the admin API. |
