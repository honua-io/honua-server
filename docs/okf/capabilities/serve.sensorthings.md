---
type: capability
title: "OGC SensorThings API"
description: "Query sensor observation data through OGC SensorThings API v1.1."
resource: "honua://capability/serve.sensorthings"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# OGC SensorThings API

Query sensor observation data through OGC SensorThings API v1.1.

| | |
| --- | --- |
| Capability key | `serve.sensorthings` |
| Category | Serve |
| Edition | Community |
| Status | preview |
| Surface maturity | 64 preview |
| Registry entries | 64 |
| Proving tests | 251 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Protocols](../../concepts/protocols.md)

## Availability

Status is **preview**. Not every preview capability is on by default, so read `serve.sensorthings` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:serve.sensorthings:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__serve.sensorthings__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
