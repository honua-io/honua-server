# Slope raster

Slope derived from the DEM.

## Slope Parameters

- **Units**: degrees

- **Z-factor**: 1

- **Spatial reference**: EPSG:3857

## Slope Statistics

- **Minimum slope**: 0.1 degrees

- **Mean slope**: 12.4 degrees

- **Maximum slope**: 64.8 degrees

## Artifacts

_Outputs produced by the workflow._
| Artifact ID | Kind | Label | URI | Content Type |
|---|---|---|---|---|
| slope-raster | Raster | Slope | honua://artifacts/slope-raster | - |

## Summary

Slope was computed in degrees using z-factor 1. Mean slope is 12.4 degrees; the maximum is 64.8 degrees.

---
- _Job_: `job-golden`
- _Result package_: `pkg-slope`
- _Processes_: surface.slope
- _Sources_: dem
- _Executed at_: 2026-04-24 09:55:00Z
- _Generated at_: 2026-04-24 10:00:00Z
