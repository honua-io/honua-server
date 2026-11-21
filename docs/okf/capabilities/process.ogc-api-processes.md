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
| Proving tests | 202 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [OGC APIs](../../reference/protocols/ogc-apis.md)

## Availability

Status is **preview**, so this capability can be off on a default install. Read `process.ogc-api-processes` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:process.ogc-api-processes:Enabled` is `true` (environment variable `Capabilities__Experimental__process.ogc-api-processes__Enabled`). Edition licensing still applies once it is enabled.
