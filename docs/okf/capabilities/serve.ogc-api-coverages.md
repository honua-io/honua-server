---
type: capability
title: "OGC API Coverages"
description: "Serve coverage data through OGC API - Coverages."
resource: "honua://capability/serve.ogc-api-coverages"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# OGC API Coverages

Serve coverage data through OGC API - Coverages.

| | |
| --- | --- |
| Capability key | `serve.ogc-api-coverages` |
| Category | Serve |
| Edition | Community |
| Status | preview |
| Surface maturity | 8 preview |
| Registry entries | 8 |
| Proving tests | 53 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [OGC APIs](../../reference/protocols/ogc-apis.md)

## Availability

Status is **preview**, so this capability can be off on a default install. Read `serve.ogc-api-coverages` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:serve.ogc-api-coverages:Enabled` is `true` (environment variable `Capabilities__Experimental__serve.ogc-api-coverages__Enabled`). Edition licensing still applies once it is enabled.
