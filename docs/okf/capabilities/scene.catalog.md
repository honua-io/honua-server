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

Status is **deferred**, so this capability can be off on a default install. Read `scene.catalog` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:scene.catalog:Enabled` is `true` (environment variable `Capabilities__Experimental__scene.catalog__Enabled`). Edition licensing still applies once it is enabled.
