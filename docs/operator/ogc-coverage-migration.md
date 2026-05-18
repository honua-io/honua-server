# OGC Coverage Migration

This page describes the first Honua migration planning slice for OGC coverage
services tracked by honua-server#1030. It covers WCS and OGC API Coverages
source inventory. It does not claim full coverage import, target publication,
or cutover parity.

## First Slice Scope

The first slice introduces `OgcCoverageMigrationInventoryScanner`, which builds
a `MigrationSourceInventoryArtifact` from structured coverage service metadata.
The scanner is intentionally contract-first: it captures what a source advertises
so the migration toolkit can classify automated, manual-review, and unsupported
coverage paths before any data is copied.

The scanner records:

- service type, version, title, provider, fees, and access constraints
- coverage identifiers, titles, descriptions, native formats, and coverage type
- CRS declarations, axis order, subset axes, axis bounds, resolution, and
  discrete allowed values
- range and band metadata, including data type, units, no-data values, and
  interpretations
- advertised output formats, with GeoTIFF and COG separated from NetCDF, HDF,
  Zarr, and unknown formats
- temporal dimensions that require explicit parity review before cutover

## Compatibility Semantics

The first automated coverage data path is GeoTIFF or Cloud Optimized GeoTIFF.
If a coverage advertises GeoTIFF or COG, the inventory marks the resource as a
candidate for automated raster migration planning.

Scientific or multidimensional outputs are classified separately:

| Source output | First-slice classification |
|---|---|
| GeoTIFF | Candidate automated path |
| Cloud Optimized GeoTIFF | Candidate automated path |
| NetCDF | Unsupported until format support lands |
| HDF/HDF5 | Unsupported until format support lands |
| Zarr | Unsupported until format support lands |
| Unknown output format | Unsupported/manual review |

Temporal dimensions, access constraints, axis-order behavior, and uncommon
subset semantics keep a resource in manual review even when a GeoTIFF or COG
path exists. This prevents rendered WMS output or partial metadata discovery
from being treated as coverage-data migration proof.

## Operator Review

Before using coverage migration language beyond inventory planning, operators
need evidence for:

- source metadata inventory reviewed with the source owner
- GeoTIFF/COG export or retrieval path validated for each automated candidate
- sample pixel/window parity across source and Honua target
- CRS, axis order, subset, no-data, band/range, and temporal metadata parity
- explicit unsupported/manual-review records for multidimensional or scientific
  formats
- cutover readiness linked from the standard migration evidence pack

Until those evidence items exist, WCS and OGC API Coverages support should be
described as inventory and migration planning, not automated end-to-end
migration.
