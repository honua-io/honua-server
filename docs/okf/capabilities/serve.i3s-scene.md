---
type: capability
title: "I3S Scene Serving"
description: "Serve I3S metadata previews through Enterprise-gated SceneServer handlers. Off by default: the routes return HTTP 404 until Capabilities:Experimental:serve.i3s-scene:Enabled=true; once enabled, unlicensed requests return HTTP 402."
resource: "honua://capability/serve.i3s-scene"
tags: [capability, serve, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# I3S Scene Serving

Serve I3S metadata previews through Enterprise-gated SceneServer handlers. Off by default: the routes return HTTP 404 until Capabilities:Experimental:serve.i3s-scene:Enabled=true; once enabled, unlicensed requests return HTTP 402.

| | |
| --- | --- |
| Capability key | `serve.i3s-scene` |
| Category | Serve |
| Edition | Enterprise |
| Status | experimental |
| Surface maturity | 16 implemented |
| Registry entries | 16 |
| Proving tests | 68 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [3D Tiles and scenes](../../reference/protocols/3d-tiles-and-scenes.md)

## Availability

Status is **experimental**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `serve.i3s-scene`: `Capabilities:Experimental:serve.i3s-scene:Enabled`, or container environment variable `Capabilities__Experimental__serve.i3s-scene__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
