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

Status is **preview**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `serve.ogc-api-edr`: `Capabilities:Experimental:serve.ogc-api-edr:Enabled`, or container environment variable `Capabilities__Experimental__serve.ogc-api-edr__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
