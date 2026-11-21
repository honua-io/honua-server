---
type: capability
title: "I3S Scene Serving"
description: "Serve I3S metadata previews through Enterprise-gated SceneServer handlers; unlicensed requests return HTTP 402."
resource: "honua://capability/serve.i3s-scene"
tags: [capability, serve, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# I3S Scene Serving

Serve I3S metadata previews through Enterprise-gated SceneServer handlers; unlicensed requests return HTTP 402.

| | |
| --- | --- |
| Capability key | `serve.i3s-scene` |
| Category | Serve |
| Edition | Enterprise |
| Status | experimental |
| Surface maturity | 14 implemented |
| Registry entries | 14 |
| Proving tests | 65 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [3D Tiles and scenes](../../reference/protocols/3d-tiles-and-scenes.md)

## Availability

Status is **experimental**. Not every experimental capability is on by default, so read `serve.i3s-scene` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:serve.i3s-scene:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__serve.i3s-scene__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
