# Rendered raster baselines

These PNGs are release-hardening contracts for the decoded pixels produced by the shared map renderer. PNG byte streams are not compared because encoder metadata and compression are not visual output.

| Baseline | Path covered | Tolerance | Rationale |
|---|---|---:|---|
| `wms-getmap-point.png` | WMS 1.3 GetMap, opaque 256×256 | RMSE 0; changed pixels 0 | The scene uses only a seeded point and fixed solid-color vector primitives. Skia output is deterministic, so fuzz would conceal a renderer regression. |
| `wms-getmap-transparent.png` | WMS 1.3 GetMap, transparent non-square image | RMSE 0; changed pixels 0 | This specifically contracts alpha/background and viewport behavior; any changed channel is significant. |
| `static-map-overlays.png` | Static map with a layer, marker, and path | RMSE 0; changed pixels 0 | The fixed geometry and solid-color overlays use no fonts, external graphics, or platform assets. |
| `mapserver-export-point.png` | GeoServices MapServer export, PNG32 | RMSE 0; changed pixels 0 | This is the same deterministic vector source through the export adapter; exact pixels prove adapter parity. |

When an intentional renderer change requires regeneration, review the image and diff first, then run only this test class with `HONUA_UPDATE_RASTER_BASELINES=1` and `HONUA_RASTER_BASELINE_ROOT` set to this directory. Never raise a tolerance merely to accept an unexplained diff.
