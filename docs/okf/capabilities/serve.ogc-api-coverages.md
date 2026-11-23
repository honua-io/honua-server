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
| Proving tests | 57 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [OGC APIs](../../reference/protocols/ogc-apis.md)

## Availability

Status is **preview**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `serve.ogc-api-coverages`: `Capabilities:Experimental:serve.ogc-api-coverages:Enabled`, or container environment variable `Capabilities__Experimental__serve.ogc-api-coverages__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
