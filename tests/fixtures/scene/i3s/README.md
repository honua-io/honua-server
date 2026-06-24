# Synthetic I3S 1.7 conformance fixture (#1813)

This directory holds a **Honua-authored** synthetic fixture for the Esri I3S
SceneServer serving surface (#1809–#1812). It is the test oracle for the
node-page / geometry / attribute hard lane.

It is **not** a vendored copy of the Esri `i3s-spec` repository: that
specification is licensed CC BY-ND and must not be redistributed here. Every
file in this directory is original Honua content that mirrors the *shapes*
described by OGC Community Standard 19-008 (I3S 1.7) closely enough to validate
the served resources, without copying spec text or example payloads.

## Contents

- `source-tileset/tileset.json` — a small 3D Tiles tileset (a root grouping node
  with two content-bearing children) that the node-page projector and the
  SceneServer endpoints consume. Mounted as a hosted scene's asset root in the
  conformance test.
- `expected/service.json` — the canonical shape of the served SceneServer
  service descriptor for the fixture scene (field presence + invariants the
  protocol-shape validator asserts).
- `expected/layer.json` — the canonical shape of the served `3dSceneLayer`
  descriptor (layerType, store.nodePages, attributeStorageInfo, geometry/material
  /texture definitions).
- `expected/nodepage-0.json` — the canonical shape of the first served node page
  (node count, obb/lodThreshold/children/mesh invariants).

## Validator

`I3sProtocolShapeValidator` (in `Honua.Protocols.Scene.Tests`) checks served I3S
resources against these structural invariants. The conformance test
`I3sConformanceFixtureTests` mounts `source-tileset/` as a scene, drives the live
SceneServer endpoints, and runs the validator over the responses.

## Manual ArcGIS smoke runbook

See [`docs/contributor/i3s-sceneserver-smoke.md`](../../../../docs/contributor/i3s-sceneserver-smoke.md)
for the manual ArcGIS Pro / `@arcgis/core` `SceneLayer` load probe used to
validate render fidelity that no headless harness can assert.
