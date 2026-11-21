---
type: capability
title: "CityGML/BIM Scene Ingest"
description: "Ingest CityGML building models into a servable Building Scene Layer 3D Tiles tileset with per-feature discipline / sub-layer semantics."
resource: "honua://capability/scene.bim-ingest"
tags: [capability, scene, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# CityGML/BIM Scene Ingest

Ingest CityGML building models into a servable Building Scene Layer 3D Tiles tileset with per-feature discipline / sub-layer semantics.

| | |
| --- | --- |
| Capability key | `scene.bim-ingest` |
| Category | Scene |
| Edition | Enterprise |
| Status | experimental |
| Surface maturity | 1 experimental |
| Registry entries | 1 |
| Proving tests | 7 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [3D Tiles and scenes](../../reference/protocols/3d-tiles-and-scenes.md)

## Availability

Status is **experimental**. Not every experimental capability is on by default, so read `scene.bim-ingest` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:scene.bim-ingest:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__scene.bim-ingest__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
