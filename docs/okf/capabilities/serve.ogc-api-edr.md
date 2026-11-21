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

Status is **preview**. Not every preview capability is on by default, so read `serve.ogc-api-edr` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:serve.ogc-api-edr:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__serve.ogc-api-edr__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
