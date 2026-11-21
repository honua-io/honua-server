---
type: capability
title: "3D Tiles Scene Serving"
description: "Serve published 3D Tiles scene layers through the SceneServer surface. Scene ingest (CityGML/point cloud) is Enterprise-gated separately."
resource: "honua://capability/serve.3d-tiles-scene"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# 3D Tiles Scene Serving

Serve published 3D Tiles scene layers through the SceneServer surface. Scene ingest (CityGML/point cloud) is Enterprise-gated separately.

| | |
| --- | --- |
| Capability key | `serve.3d-tiles-scene` |
| Category | Serve |
| Edition | Community |
| Status | experimental |
| Surface maturity | 6 experimental |
| Registry entries | 6 |
| Proving tests | 78 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [3D Tiles and scenes](../../reference/protocols/3d-tiles-and-scenes.md)

## Availability

Status is **experimental**, so this capability can be off on a default install. Read `serve.3d-tiles-scene` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:serve.3d-tiles-scene:Enabled` is `true` (environment variable `Capabilities__Experimental__serve.3d-tiles-scene__Enabled`). Edition licensing still applies once it is enabled.
