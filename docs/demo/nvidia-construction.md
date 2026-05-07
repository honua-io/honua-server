# NVIDIA Construction Demo Fixture

A deterministic, local-first construction site fixture that Honua Server
serves as hosted 3D Tiles plus an observations sidecar. Designed for the
NVIDIA demo so the web client and SDK work can proceed without AWS, Azure,
Cesium ion, or any live drone/point-cloud pipeline.

> Status: prebuilt placeholder geometry committed by `honua-server-898`. A
> deterministic generation path that produces a real GLB tileset for the
> same `nvidia-construction` scene id has landed in `honua-server-899` —
> see [3D Tiles Generation Pipeline](../gis/scene-generation.md#demo-fixture-nvidia-construction-site-899)
> for the prebuilt-vs-generated registry toggle (Postgres-first composite,
> with `Scenes:Datasets` as the fallback) and the seed entry that drives
> `POST /api/v1/admin/scenes/generate`. Production-scale tiling, LOD, and
> BIM/point-cloud inputs remain tracked in `honua-server-842`.

## What ships

Two registered scene datasets, sharing one fixture directory under
`tests/fixtures/scenes/nvidia-construction/`:

| Scene ID | Layer kind | Tileset file | Purpose |
|---|---|---|---|
| `nvidia-construction` | `structure` | `tileset.json` | Main 3D structure tileset with project metadata in `extras` |
| `nvidia-construction-obs` | `observations` | `obs-tileset.json` | Observation/issue pins layer; references `observations.json` sidecar |

Fixture layout:

```
tests/fixtures/scenes/nvidia-construction/
├── tileset.json          # registered as nvidia-construction (structure layer)
├── obs-tileset.json      # registered as nvidia-construction-obs (observations layer)
├── observations.json     # stable sidecar (5 observations, schemaVersion 1.0)
└── tiles/
    ├── structure.b3dm    # placeholder b3dm; same minimal stub used by other fixtures
    └── obs-pin.b3dm      # placeholder b3dm for observation pins
```

## Running locally

`appsettings.Development.json` registers both scenes via
`Scenes:Datasets`. The `ConfigurationSceneDatasetRegistry` resolves the
relative `AssetRoot` (`../../tests/fixtures/scenes/nvidia-construction`)
against `IHostEnvironment.ContentRootPath` (`src/Honua.Server/`), so the
fixture is portable across `dotnet run` from the repo root or the project
directory.

The Development profile leaves `DataSource:Provider` unset, which means the
Postgres-backed scene registry (#844) is composed in front of the
configuration registry as `CompositeSceneDatasetRegistry`. The composite
queries Postgres first and falls through to the configuration entries when
the slug is not owned by Postgres — so the demo scene IDs are served via the
fallback path. Postgres must therefore be reachable at
`ConnectionStrings:DefaultConnection` for `dotnet run` to serve the demo;
without it, the composite raises HTTP 500 on the first Postgres query before
the fallback runs.

```bash
# Reusable external PostGIS on port 5433 with database honua_test and
# user/password test/test — matches the Development profile's
# ConnectionStrings:DefaultConnection out of the box.
docker compose -f docker/docker-compose.test-db.yml up -d

dotnet run --project src/Honua.Server
```

The hermetic on-disk fixture is verified by
`NvidiaConstructionFixtureFileTests` (no web host, no Postgres, fast tier).
Hosted-endpoint coverage in `NvidiaConstructionFixtureTests` registers the
same fixture via in-memory configuration through `WebAppFixture`, which
provisions a test-managed Postgres testcontainer so the configuration
registry composes behind the same `CompositeSceneDatasetRegistry` that
production runs.

| URL | What it returns |
|---|---|
| `http://localhost:5000/scenes/nvidia-construction/tileset.json` | Main tileset with `extras.cameraHint`, `extras.bounds`, and `extras.projectMeta` (project metadata; nests `workPackages`, `parcels`, and `stakeholders`) |
| `http://localhost:5000/scenes/nvidia-construction/tiles/structure.b3dm` | Placeholder structure tile |
| `http://localhost:5000/scenes/nvidia-construction-obs/tileset.json` | Observations layer tileset (shares fixture root, different `TilesetFileName`) |
| `http://localhost:5000/scenes/nvidia-construction-obs/tiles/obs-pin.b3dm` | Placeholder observation pin tile |
| `http://localhost:5000/scenes/nvidia-construction/observations.json` | Observations sidecar (also reachable via `nvidia-construction-obs` since both scenes share `AssetRoot`) |

There is no state to reset; the fixture is static files plus configuration.

### Resetting / rebuilding

The fixture is fully deterministic and committed to the repo. To "reset",
just `git restore tests/fixtures/scenes/nvidia-construction/`. No database
or storage cleanup is involved.

## Cesium client smoke path

The fixture is intended to load in CesiumJS without ion or any remote
asset host. Use the built-in `EllipsoidTerrainProvider` because the demo
does not yet ship a terrain dataset (terrain serving belongs to
`honua-server-839`).

```html
<script type="module">
  import { Viewer, Cesium3DTileset, EllipsoidTerrainProvider, Cartesian3, Math as CMath } from 'cesium';

  const viewer = new Viewer('cesiumContainer', {
    terrainProvider: new EllipsoidTerrainProvider(),
  });

  // Main 3D structure layer
  const structure = await Cesium3DTileset.fromUrl(
    'http://localhost:5000/scenes/nvidia-construction/tileset.json'
  );
  viewer.scene.primitives.add(structure);

  // Observation pins layer
  const observations = await Cesium3DTileset.fromUrl(
    'http://localhost:5000/scenes/nvidia-construction-obs/tileset.json'
  );
  viewer.scene.primitives.add(observations);

  // Honor the camera hint stored in extras
  const hint = structure.extras?.cameraHint;
  if (hint) {
    viewer.scene.camera.setView({
      destination: Cartesian3.fromDegrees(hint.longitude, hint.latitude, hint.height),
      orientation: {
        heading: CMath.toRadians(hint.heading),
        pitch: CMath.toRadians(hint.pitch),
        roll: CMath.toRadians(hint.roll),
      },
    });
  }

  // Fetch observations sidecar in parallel
  const obs = await fetch('http://localhost:5000/scenes/nvidia-construction/observations.json')
    .then(r => r.json());
  console.log(`${obs.observations.length} observations loaded`);
</script>
```

## Coordinate units (read this before integrating)

The `extras` block and the OGC bounding region use different units; this is
intentional and matches Cesium and 3D Tiles conventions:

- **`root.boundingVolume.region`** — six numbers
  `[west, south, east, north, minHeight, maxHeight]` with longitude/latitude
  in **radians** (WGS-84). Required by the OGC 3D Tiles 1.1 specification.
- **`extras.cameraHint`** and **`extras.bounds`** — decimal **degrees**, by
  Cesium client convention. Convert with `Cesium.Math.toRadians` only if the
  client needs to feed them into a region-shaped API.
- **`observations.json`** — decimal **degrees** for `longitude`/`latitude`,
  meters for `elevation`.

## Tileset extras schema

Top-level `extras` in `tileset.json`:

| Field | Type | Notes |
|---|---|---|
| `attribution` | string | Required for client display |
| `layerKind` | `"structure"` \| `"observations"` | Used by clients to pick a renderer |
| `layerId` | string | Stable; matches the scene ID in URLs |
| `cameraHint` | object | `longitude`, `latitude`, `height` (m), `heading`, `pitch`, `roll` (degrees) |
| `bounds` | object | `west`, `south`, `east`, `north` (degrees), `minHeight`, `maxHeight` (m) |
| `projectMeta` | object | `id`, `name`, `phase`, `completionRatio`, `startDate`, `expectedEndDate`, `workPackages`, `parcels`, `stakeholders` |
| `observationsLayer` | object (main only) | `sidecarUri`, `tilesetSceneId` — pointers to the obs layer & sidecar |
| `observationsSidecar` | object (obs only) | `uri`, `schemaVersion` — pointer to `observations.json` |

## Observations sidecar schema (`observations.json`)

```jsonc
{
  "schemaVersion": "1.0",
  "sceneId": "nvidia-construction",
  "projectId": "nvidia-construction-demo-2026",
  "attribution": "© Honua Demo 2026",
  "generatedAt": "2026-05-01T00:00:00Z",
  "observations": [
    {
      "id": "obs-001",
      "kind": "safety_issue",        // safety_issue | progress | deviation | material_delivery
      "status": "open",              // open | resolved
      "severity": "high",            // info | medium | high
      "title": "Unprotected edge at grid F-7",
      "description": "...",
      "longitude": -121.9685,
      "latitude": 37.3714,
      "elevation": 12.5,             // meters
      "recordedAt": "2026-04-10T08:23:00Z",
      "evidenceKind": "field_photo", // field_photo | drone_capture | measurement
      "evidenceCount": 3,
      "evidenceUris": ["evidence/obs-001-photo-1.jpg", ...],
      "workPackageId": "wp-structure",
      "assignee": "site-inspector-01"
    }
    // … 4 more entries
  ]
}
```

The `evidenceUris` paths are relative to the scene asset root; they are
listed for client framing only — the demo does not commit binary evidence
files.

## Terrain

No terrain tileset is committed. The demo client should use Cesium's
`EllipsoidTerrainProvider`; real terrain serving is tracked in
`honua-server-839`.

## OpenUSD / Omniverse handoff

This fixture is served as hosted OGC 3D Tiles to CesiumJS. It can also be
exported through the Pro preview USDA stage manifest route at
`/scenes/nvidia-construction/exports/openusd/stage.usda`, which preserves scene
metadata and source references for inspection in NVIDIA tooling. This remains
metadata-only; native USD geometry conversion, Omniverse/Nucleus publishing,
and Unreal runtime integration are still separate follow-ups documented in
[OpenUSD and Omniverse Export Path](../gis/openusd-omniverse-export-path.md)
(`honua-server-901`, first artifact `honua-server-904`).

## Drone / point-cloud / reality-capture handoff

The placeholder b3dm tiles in this fixture stand in for real drone or
reality-capture output. The recommended first ingest path — pre-tiled 3D
Tiles registered through the existing scene dataset registry, with bounded
follow-ups for COPC streaming and CPU/GPU PDAL conversion — is documented in
[Point Cloud, Drone, and Reality-Capture Ingest](../gis/point-cloud-reality-capture-ingest.md)
(`honua-server-900`). Replacing the fixture's placeholders with real
captures is a content swap; no code changes are required to serve them.

## Testing

Tests are split so the on-disk fixture invariants run on the fast tier
without booting a server, while the hosted-endpoint coverage stays in the
integration class with a test-managed Postgres testcontainer:

`tests/dotnet/Honua.Server.Tests/Features/Protocols/Scene/NvidiaConstructionFixtureFileTests.cs`
(fast tier, no web host, no Postgres) covers:

- both tilesets parse, declare `asset.version == "1.1"`, and have valid
  bounding regions in WGS-84 radians;
- required `extras` fields are present (camera hint, bounds, project meta,
  layer kind, layer id);
- every observation in `observations.json` falls within the `extras.bounds`
  declared on both tilesets, so the published extent stays consistent with
  the sidecar coordinates clients render;
- `observations.json` has 5 unique stable IDs and required per-entry fields;
- both b3dm placeholders start with the `b3dm` magic header;
- content URIs are safe relative paths (no traversal, no remote URLs).

`tests/dotnet/Honua.Server.Tests/Features/Protocols/Scene/NvidiaConstructionFixtureTests.cs`
(integration tier, `WebAppFixture` + Postgres testcontainer) covers:

- both scene IDs serve their tilesets and the sidecar via the hosted scene
  endpoints, with `nvidia-construction-obs` resolving to `obs-tileset.json`
  even though it shares the same `AssetRoot`.

Run only this fixture's tests:

```bash
dotnet test tests/dotnet/Honua.Server.Tests \
  --filter "FullyQualifiedName~NvidiaConstructionFixture"
```
