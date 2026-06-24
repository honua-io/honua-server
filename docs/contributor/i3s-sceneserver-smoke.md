# I3S SceneServer manual smoke runbook (#1813)

This runbook is the manual oracle for the parts of the Esri I3S SceneServer
surface that no headless harness can assert: render fidelity in ArcGIS Pro and
`@arcgis/core`. The automated `I3sConformanceFixtureTests` harness validates the
protocol *shapes* (descriptor / node pages / statistics); this runbook validates
that a real SceneLayer client *loads and traverses* the served scene.

## Scope (what landed)

- `…/SceneServer` and `…/SceneServer/layers/0` descriptors (service + layer),
  Enterprise-gated, at the canonical `/rest/services/{id}/SceneServer` path and
  the `/scenes/{id}/SceneServer` alias.
- `…/layers/0/nodepages/{n}` — I3S 1.7 node pages projected from the hosted 3D
  Tiles node tree (#1809): oriented bounding boxes in the index CRS, LOD
  thresholds from geometric error, parent/child references by global node index,
  and per-node geometry/attribute/material resource references.
- `…/layers/0/statistics/f_{n}/0` — per-field statistics summary (#1811).
- `layerType` mapping for `3DObject` / `Building` / `PointCloud` (#1812).

## Deferred (NOT yet served — see #1810 / #1811)

The descriptor and node pages reference geometry / attribute / texture resources
by index, but the **binary node resources are not yet served**:

- `…/nodes/{id}/geometries/0` — the glTF → I3S interleaved geometry-buffer
  transcoder (positions / normals / UVs / colours / feature-ids; vertexCRS +
  per-node MBS offset) is the deferred XL lane (#1810).
- `…/nodes/{id}/textures/0` — texture serving (#1810).
- `…/nodes/{id}/attributes/f_{n}/0` — native per-field binary attribute files
  (#1811).

A SceneLayer client will therefore discover the layer, traverse the node tree,
and read field statistics, but will not render geometry until #1810/#1811 land.
The node-page references are deliberately honest about which resources exist:
they advertise the geometry/attribute index a client *would* fetch, matching the
descriptor, but the corresponding binary routes are the tracked follow-up.

## Prerequisites

1. An Enterprise-licensed Honua server (`HonuaEdition.Enterprise`).
2. A registered hosted scene with a loadable `tileset.json` (any 3D Tiles scene
   the server already serves, e.g. the demo Maui buildings scene).
3. ArcGIS Pro 3.x **or** a local page importing `@arcgis/core` ≥ 4.29.

## Procedure — `@arcgis/core` SceneLayer load probe

```js
import SceneLayer from "@arcgis/core/layers/SceneLayer.js";

const layer = new SceneLayer({
  url: "https://<host>/rest/services/<sceneId>/SceneServer/layers/0",
});

await layer.load();
console.log("loaded", layer.title, layer.geometryType);
```

Expected (this PR):

- `layer.load()` resolves (the descriptor is conformant and advertises
  `store.nodePages`).
- The layer view requests `nodepages/0` and receives a valid node page (no 404).
- Field statistics resolve at `…/statistics/f_0/0`.

Known gap (until #1810/#1811):

- The view will request `nodes/{id}/geometries/0` and receive 404 — geometry
  does not render. This is expected and tracked.

## Procedure — ArcGIS Pro

1. Add Data → From Path → paste the `…/SceneServer` URL.
2. Confirm the scene layer appears in the Contents pane with the correct
   `layerType` (3D Object / Building / Point Cloud).
3. Confirm Pro traverses node pages without descriptor errors.
4. Geometry render and Identify are blocked until #1810/#1811.

Record the Pro / browser version and the observed behaviour in the PR or the
epic (#1805) when running this probe.
