---
type: capability
title: "Point Cloud Scene Ingest"
description: "Ingest LAS point clouds into a servable 3D Tiles point tileset preserving per-point classification, intensity, and RGB."
resource: "honua://capability/scene.pointcloud-ingest"
tags: [capability, scene, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Point Cloud Scene Ingest

Ingest LAS point clouds into a servable 3D Tiles point tileset preserving per-point classification, intensity, and RGB.

| | |
| --- | --- |
| Capability key | `scene.pointcloud-ingest` |
| Category | Scene |
| Edition | Enterprise |
| Status | experimental |
| Surface maturity | 1 experimental |
| Registry entries | 1 |
| Proving tests | 11 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [3D Tiles and scenes](../../reference/protocols/3d-tiles-and-scenes.md)

## Availability

Status is **experimental**. Not every experimental capability is on by default, so read `scene.pointcloud-ingest` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:scene.pointcloud-ingest:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__scene.pointcloud-ingest__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
