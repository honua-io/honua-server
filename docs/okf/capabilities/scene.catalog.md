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
| Status | experimental |
| Surface maturity | 9 deferred |
| Registry entries | 9 |
| Proving tests | 41 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md)

## Availability

Status is **experimental**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `scene.catalog`: `Capabilities:Experimental:scene.catalog:Enabled`, or container environment variable `Capabilities__Experimental__scene.catalog__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
