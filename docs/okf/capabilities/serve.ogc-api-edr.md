---
type: capability
title: "OGC API - EDR"
description: "Query environmental data resources through OGC API - Environmental Data Retrieval."
resource: "honua://capability/serve.ogc-api-edr"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# OGC API - EDR

Query environmental data resources through OGC API - Environmental Data Retrieval.

| | |
| --- | --- |
| Capability key | `serve.ogc-api-edr` |
| Category | Serve |
| Edition | Community |
| Status | preview |
| Surface maturity | 6 preview |
| Registry entries | 6 |
| Proving tests | 48 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [OGC APIs](../../reference/protocols/ogc-apis.md)

## Availability

Status is **preview**, so this capability can be off on a default install. Read `serve.ogc-api-edr` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:serve.ogc-api-edr:Enabled` is `true` (environment variable `Capabilities__Experimental__serve.ogc-api-edr__Enabled`). Edition licensing still applies once it is enabled.
