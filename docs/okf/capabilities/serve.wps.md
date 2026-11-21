---
type: capability
title: "WPS 2.0"
description: "Submit and poll processes through OGC WPS 2.0 (GetCapabilities, DescribeProcess, Execute, GetStatus, GetResult)."
resource: "honua://capability/serve.wps"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# WPS 2.0

Submit and poll processes through OGC WPS 2.0 (GetCapabilities, DescribeProcess, Execute, GetStatus, GetResult).

| | |
| --- | --- |
| Capability key | `serve.wps` |
| Category | Serve |
| Edition | Community |
| Status | preview |
| Surface maturity | 3 preview |
| Registry entries | 3 |
| Proving tests | 31 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Protocols](../../concepts/protocols.md)

## Availability

Status is **preview**. Not every preview capability is on by default, so read `serve.wps` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:serve.wps:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__serve.wps__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
