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

Status is **preview**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `serve.sensorthings`: `Capabilities:Experimental:serve.sensorthings:Enabled`, or container environment variable `Capabilities__Experimental__serve.sensorthings__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
