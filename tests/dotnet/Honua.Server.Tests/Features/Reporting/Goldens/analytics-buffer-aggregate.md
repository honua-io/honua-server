# Buffered places

500m buffers applied to the seed places layer.

## Buffer Parameters

- **Buffer distance**: 500 meters

- **Buffered features**: 42

- **Dissolved groups**: 7

- **Total buffered area**: 123456.789 m²

## Artifacts

_Outputs produced by the workflow._
| Artifact ID | Kind | Label | URI | Content Type |
|---|---|---|---|---|
| buffered-layer | FeatureLayer | Buffered places | honua://artifacts/buffered-layer | application/geo+json |

## Assumptions

| Assumption |
|---|
| Places layer is in EPSG:4326. |

## Summary

This run applied a buffer of 500 meters, buffered 42 feature(s) and dissolved them into 7 group(s) covering 123456.789 m².

---
- _Job_: `job-golden`
- _Result package_: `pkg-buffer`
- _Processes_: analytics.buffer-aggregate
- _Sources_: places
- _Executed at_: 2026-04-24 09:55:00Z
- _Generated at_: 2026-04-24 10:00:00Z
