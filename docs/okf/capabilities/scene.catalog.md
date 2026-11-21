---
type: capability
title: "Scene Catalog"
description: "Discover published 3D scene layers through the scene catalog surface."
resource: "honua://capability/scene.catalog"
tags: [capability, scene, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Scene Catalog

Discover published 3D scene layers through the scene catalog surface.

| | |
| --- | --- |
| Capability key | `scene.catalog` |
| Category | Scene |
| Edition | Community |
| Status | deferred |
| Surface maturity | 3 deferred |
| Registry entries | 3 |
| Proving tests | 15 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md)

## Availability

Status is **deferred**. Not every deferred capability is on by default, so read `scene.catalog` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:scene.catalog:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__scene.catalog__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
