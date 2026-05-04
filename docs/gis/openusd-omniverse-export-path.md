# OpenUSD and Omniverse Export Path for Honua Scenes

This page is a research-spike output for `honua-server-901`. It maps Honua's
hosted scene model to OpenUSD and Omniverse concepts, chooses the first safe
artifact, and defines the server contracts required before implementation.

**No production OpenUSD exporter, USD conversion pipeline, Omniverse connector,
or Unreal runtime integration is added by this spike.** Honua's current 3D
demo path remains hosted OGC 3D Tiles consumed by CesiumJS.

## Recommendation

The first artifact should be a **USDA text-format stage manifest** generated
from a registered Honua scene.

USDA is the right first slice because it is human-readable, inspectable in
OpenUSD tools, small enough to review in demos, and can describe scene
organization, metadata, and external source references without pretending that
Honua has converted 3D Tiles, imagery, vectors, observations, or terrain into
native USD geometry. It is a credibility artifact, not a runtime replacement
for CesiumJS.

Recommended initial output shape:

- A deterministic `.usda` root layer with `defaultPrim`, `metersPerUnit = 1`,
  `upAxis = "Z"`, and `customLayerData` carrying Honua scene identity,
  attribution, export schema version, source URLs, extent, and CRS metadata.
- A root `Xform` or `Scope` for the scene and child prims for terrain,
  structure tiles, imagery, vector overlays, observations, and project
  metadata. The first version stores source pointers and metadata in
  `customData`; it does not author converted `UsdGeomMesh`, `UsdShade`, or
  point-instancer payloads.
- Optional variant-set placeholders for progress/time states only when the
  source scene already declares stable time or phase metadata. No synthetic
  time simulation is invented by the exporter.
- A `usdchecker` validation step in the future implementation issue, using
  OpenUSD tooling outside the server hot path. The server implementation
  should not add the OpenUSD runtime as a request-path dependency unless a
  later exporter actually needs authored USD composition behavior.

This path is credible enough for a bounded implementation slice because it
exposes the NVIDIA ecosystem handoff without promising live Omniverse sync or
lossless 3D Tiles to USD conversion.

## Responsibility boundaries

| Surface | Responsibility | Not responsible for in this slice |
| --- | --- | --- |
| OpenUSD | Scene-description interchange model. The USDA manifest organizes Honua scene components, preserves inspection metadata, and gives downstream tools a stable place to attach later converted USD assets. | Streaming geospatial tiles, serving protected Honua assets, resolving browser access envelopes, or guaranteeing render parity with CesiumJS. |
| Omniverse | NVIDIA application and collaboration ecosystem that can consume USD assets and, in a future connector, could use Nucleus, Omniverse URLs, live layers, or connector SDKs. | Server-side Honua scene serving, token issuance, cache invalidation, or live connector behavior in this spike. |
| CesiumJS | Current first-demo runtime for hosted 3D Tiles. It loads `/scenes/{sceneId}/tileset.json`, nested assets, and observation sidecars directly from Honua. | USD authoring, Omniverse collaboration, or Unreal gameplay/runtime integration. |
| Unreal | Future runtime/editor consumer owned by the Unreal/.NET SDK workstream. Unreal may consume USD through native USD support or an Omniverse connector, but server-side export should stay client-agnostic. | Defining Honua's server export contract or replacing the CesiumJS demo path. |

## Scene component mapping

| Honua scene component | Current source | OpenUSD candidate | First-artifact treatment | Loss or risk |
| --- | --- | --- | --- | --- |
| Scene registration | Scene dataset registry or `Scenes:Datasets` fallback | Root layer metadata and root prim custom data | Preserve `sceneId`, display name, description, attribution, edition/access notes, source URLs, and schema version | Registry fields that are not stable public contracts must not leak. |
| Terrain references | Terrain-RGB / elevation APIs, future terrain scene assets | `Scope` or `Xform` with terrain source metadata; later `UsdGeomMesh` or heightfield-derived mesh if a converter exists | Reference terrain endpoint or declare absent terrain for the NVIDIA fixture | No vertical datum transformation; meters-only assumption must be explicit. |
| 3D Tiles structure | Hosted `tileset.json` and nested B3DM/GLB/texture assets | `Xform` grouping prim; later payloads or references to converted USD mesh layers | Store the 3D Tiles root URL, asset version, bounds, geometric role, and source format metadata | 3D Tiles streaming, LOD, batch metadata, and per-tile transforms are not converted. |
| Meshes and model assets | B3DM/GLB under a scene asset root | Future `UsdGeomMesh`, material bindings, or payloaded converted layers | Metadata-only source pointer | Material fidelity, KTX2/Basis texture handling, instancing, and feature IDs are deferred. |
| Imagery | Raster/COG/tile services or future scene overlays | Future `UsdShade` texture/material assignments or image-plane layers | Metadata-only source pointer when present | USD image/material authoring is client/toolchain-specific and not automatic. |
| Vectors | FeatureServer, OGC API Features, MVT, or scene sidecars | Future `UsdGeomBasisCurves`, `UsdGeomMesh`, `PointInstancer`, or custom schema | Metadata-only source pointer and layer role | Geometry conversion depends on CRS, scale, styling, and feature identity rules. |
| Observations | `observations.json` sidecar in the NVIDIA fixture | `Scope` containing observation prim placeholders, later point instancers or time-sampled prims | Preserve sidecar URI, schema version, stable IDs, status/severity/work-package metadata, and coordinate unit assumptions | Evidence files are not embedded; protected evidence URLs must not be materialized with tokens. |
| Project metadata | `tileset.json` `extras.projectMeta` and sidecars | Root custom data and project `Scope` | Preserve project ID, phase, completion ratio, work packages, parcels, and stakeholders when present | Non-public or customer-sensitive fields require an allowlist before export. |
| Time/progress variants | Project phase, completion ratio, observation `recordedAt`, future progress states | Variant sets or time samples | Declare only existing stable time/progress fields; no generated simulation | Variant semantics need a later review with Unreal and Omniverse consumers. |

## Required server and export contracts

Any future implementation must define these contracts before writing exporter
code:

| Contract | Requirement |
| --- | --- |
| Export entrypoint | A bounded route or admin action that resolves a scene by URL slug, for example `GET /scenes/{sceneId}/exports/openusd/stage.usda`. This route is future work and must have endpoint-level integration tests if implemented. |
| Output format | Text USDA only for v1. Do not emit USDC, USDZ, converted meshes, package bundles, or Omniverse URLs in the first server slice. |
| Schema version | Include a Honua export schema version in `customLayerData`, independent of OpenUSD's own file version, so downstream tools can reject incompatible manifests. |
| Source URL policy | Emit stable Honua scene URLs, not filesystem paths. Include absolute URLs only after host/scheme resolution has passed through the same link-building rules used by scene resolution and admin APIs. |
| Access control | Reuse the existing scene access policy. Public scenes may include public asset URLs. Protected scenes must not embed bearer tokens, API keys, access-envelope tokens, signed cookies, or secret references. A protected manifest may point to the access-envelope contract and require the consumer to mint its own short-lived token. |
| Cache keys | Vary by scene id, host/scheme, export schema version, access policy, resolved scene revision or source ETag inputs, and `Accept`. Protected output must be private or no-store according to the scene policy. |
| Metadata allowlist | Export only approved scene metadata fields: scene identity, attribution, CRS/extent/unit metadata, project summary, work package IDs/names, observation IDs/status/severity/timestamps, and source URIs. Customer fields require explicit allowlisting before export. |
| Coordinate contract | Preserve WGS-84 longitude/latitude and meter height assumptions from the hosted scene docs. 3D Tiles `boundingVolume.region` uses radians; `extras.bounds`, `cameraHint`, and `observations.json` use decimal degrees plus meters. The USDA manifest must state `metersPerUnit = 1` and document unknown vertical datum as unknown, not transformed. |
| Validation | Validate scene existence, exporter enablement, supported `tileset.json` shape, safe source URIs, expected fixture sidecar schema, coordinate ranges, and metadata allowlist before emitting USDA. Use shared problem/error helpers for failures. |
| Serialization | Use source-generated JSON for reading scene `tileset.json` extras and sidecars. USDA text generation can be a small deterministic writer; do not add reflection-heavy OpenUSD bindings for the manifest-only slice. |
| Telemetry | Add an observable operation such as `scene.openusd.manifest` with `sceneId`, `protocol = "openusd"`, result size, protected/public classification, validation failure reason, and exception status. |
| Tests | Add fast unit tests for deterministic USDA emission and integration tests for the export route, including public/protected scenes, missing sidecars, invalid coordinate metadata, and pitch-safe absence from existing runtime capabilities until implemented. |

## Non-goals

- Production OpenUSD exporter implementation in this spike.
- Any current claim that Honua supports OpenUSD export or Omniverse
  integration.
- Omniverse live connector, Nucleus publishing, live USD layers, or
  collaborative editing.
- USDC or USDZ packaging.
- 3D Tiles, B3DM, GLB, KTX2, imagery, FeatureServer, MVT, or observation
  geometry conversion to native USD geometry.
- Unreal runtime/editor integration or packaging behavior.
- Replacement of CesiumJS as the first NVIDIA demo client.
- Embedding credentials, signed URLs, or short-lived access-envelope tokens in
  exported artifacts.

## Risks and tradeoffs

- **Overclaim risk.** A USDA manifest can be opened and inspected, but it is
  not a render-complete converted USD scene. Pitch language must say "documented
  path" or "proposed first artifact", not "OpenUSD supported".
- **Geospatial precision.** USD tools are not inherently geospatial servers.
  Any future native geometry conversion must define origin, ECEF/local-frame,
  CRS, vertical datum, and unit handling before authoring meshes.
- **Streaming mismatch.** 3D Tiles LOD and HTTP streaming do not map directly
  to USD payloads or references. The first slice intentionally avoids that
  conversion.
- **Protected asset handling.** Omniverse and desktop USD tools may not know
  how to mint Honua access envelopes. The manifest can document the access
  contract, but clients still need integration work.
- **Material fidelity.** glTF PBR and USDShade/MDL translation is a separate
  content-pipeline decision. The manifest should preserve source material
  references without claiming visual parity.
- **Dependency weight.** Pulling OpenUSD runtime libraries into the server
  would be expensive for startup, memory, trimming, and Native AOT. Keep v1 as
  deterministic text generation.

## Pitch-safe language

Use this wording:

> Honua serves the NVIDIA construction demo today as hosted OGC 3D Tiles for
> CesiumJS. The OpenUSD/Omniverse path is documented as a conservative next
> slice: a USDA text-format stage manifest that preserves scene metadata and
> source references for external inspection before any native USD conversion or
> Omniverse connector work is claimed.

Avoid this wording:

> Honua supports OpenUSD export.
>
> Honua integrates with Omniverse.
>
> Honua converts 3D Tiles to USD.
>
> Unreal can load Honua scenes through this server feature today.

## Proposed implementation sequence and child issues

The path is credible enough to create a bounded Honua Server implementation
issue for the first artifact only. Larger ecosystem work remains separate.

| # | Title | Repo | Scope | Depends on | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | `feat(scene/openusd): emit USDA stage manifest for hosted scenes` | `honua-server` | Export route/admin action, deterministic USDA writer, source-generated JSON readers for allowed scene extras/sidecars, endpoint tests, protected-scene behavior, telemetry, and docs update from "roadmap" to "preview" only if implemented. | `honua-server-901`; uses the `honua-server-898` fixture as the first input. | Filed as `honua-server-904`. |
| 2 | `feat(scene/openusd): converted USD geometry bundle spike` | `honua-server` or content-pipeline repo TBD | Decide whether conversion belongs in server, offline tooling, or a separate pipeline. Evaluate GLB/B3DM to `UsdGeomMesh`, materials, CRS frame, and `usdchecker`/visual validation. | Child #1 plus `honua-server-899` / `honua-server-842`. | Not filed by default; needs grooming after child #1. |
| 3 | Unreal consumer follow-up | `honua-sdk-dotnet` | Consume a USDA manifest or later USD bundle in Unreal workflows. | Child #1. | Tracked outside this repo by `honua-sdk-dotnet-129`; no server edits here. |
| 4 | Omniverse connector follow-up | TBD | Nucleus publishing, live layers, connector SDK use, and Omniverse URL behavior. | Child #1 and a real customer workflow. | Not filed from this server spike; needs a bounded owning repo before work starts. |

## Acceptance-criteria mapping

| Acceptance criterion (from `honua-server-901`) | Where addressed |
| --- | --- |
| A short technical recommendation exists with a chosen first artifact | "Recommendation" chooses a USDA text-format stage manifest. |
| Required server/export contracts are listed | "Required server and export contracts" lists entrypoint, format, auth, cache, metadata, geodesy, validation, serialization, telemetry, and tests. |
| The recommendation distinguishes OpenUSD, Omniverse, CesiumJS, and Unreal responsibilities | "Responsibility boundaries" separates the four surfaces. |
| Follow-up implementation issues are created only if the path is credible | "Proposed implementation sequence and child issues" records the bounded first-artifact issue filed as `honua-server-904`; broader work remains unfiled until groomed. |
| Pitch language does not imply current OpenUSD or Omniverse support unless implemented | Opening warning, "Non-goals", and "Pitch-safe language" explicitly avoid current-support claims. |

## References

- [OpenUSD introduction and concepts](https://openusd.org/docs/index.html)
- [OpenUSD USDZ file-format specification](https://openusd.org/release/spec_usdz.html)
- [NVIDIA Learn OpenUSD file-format overview](https://docs.nvidia.com/learn-openusd/latest/stage-setting/usd-file-formats.html)
- [NVIDIA Omniverse USD Connections overview](https://docs.omniverse.nvidia.com/connect/latest/overview.html)
- [NVIDIA Omniverse connector development overview](https://docs.omniverse.nvidia.com/connect/latest/developing-connectors.html)
- [Cesium 3D Tiles overview](https://cesium.com/3d-tiles/)
- Honua scene references: [Hosted 3D Tiles Scenes](scenes-3dtiles.md), [NVIDIA Construction Demo Fixture](../demo/nvidia-construction.md), and [Scene Dataset Registry](../admin-api/scene-dataset-registry.md).
- Follow-up issue: `honua-server-904`.
