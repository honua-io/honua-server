---
type: capability
title: "OGC API Processes"
description: "Submit and poll jobs through OGC API - Processes."
resource: "honua://capability/process.ogc-api-processes"
tags: [capability, process, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# OGC API Processes

Submit and poll jobs through OGC API - Processes.

| | |
| --- | --- |
| Capability key | `process.ogc-api-processes` |
| Category | Process |
| Edition | Community |
| Status | preview |
| Surface maturity | 10 preview |
| Registry entries | 10 |
| Proving tests | 204 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [OGC APIs](../../reference/protocols/ogc-apis.md)

## Availability

Status is **preview**. Not every preview capability is on by default, so read `process.ogc-api-processes` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:process.ogc-api-processes:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__process.ogc-api-processes__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
