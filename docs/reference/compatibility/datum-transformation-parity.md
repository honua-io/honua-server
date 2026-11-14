---
type: reference
title: "Datum transformation parity"
description: "Datum and vertical transformation selection, explicit pipelines and GeoServices compatibility."
---

# Datum / vertical transformation fidelity (Esri-default parity)

Honua reprojects geometry with PostGIS' embedded PROJ engine. The accuracy gap this
document addresses is **pipeline selection**, not engine capability: a bare
`ST_Transform(geom, toSrid)` lets PROJ choose its own "best available" pipeline, which
does not always match the geographic (datum) transformation ArcGIS selects by default.
That divergence is small but real — roughly 1–2 m for NAD83↔WGS84 and larger for NAD27.

To close the gap, Honua models the Esri default geotransformations as an auditable
data table and drives PostGIS' `ST_TransformPipeline(geom, '<proj-pipeline>', toSrid)`
so the selected (Esri-parity) pipeline is used.

## How it works

- `IDatumTransformationCatalog` (`Honua.Core.Features.Infrastructure.Crs`) resolves a
  `(fromSrid, toSrid)` reprojection to a `DatumTransformationSelection` carrying the
  Esri WKID, EPSG operation code, and explicit PROJ pipeline string.
- `GeoServicesDatumTransformationCatalog` is backed by the embedded table
  `src/Honua.Core/Core/Features/Infrastructure/Crs/Resources/esri-default-datum-transformations.json`.
- The GeoServices FeatureServer query handler:
  - **Honors** a client-supplied `datumTransformation` (bare WKID or
    `{"geoTransforms":[{"wkid":...,"transformForward":...}]}`) by resolving it against
    the catalog. An unknown or inapplicable WKID returns an **explicit** Esri-style
    error — it is never silently substituted.
  - **Defaults** to the catalog's Esri default for the layerSR→outSR pair when the
    client sends no `datumTransformation`.
  - Surfaces support via `supportsQueryWithDatumTransformation` in layer metadata.
- All output-CRS `ST_Transform` SQL call sites route through the single
  `DatumTransformSql.BuildTransformExpression` chokepoint so the SRID-only versus explicit-operation choice
  stays consistent and cannot drift.

## Tolerance table

Parity is asserted against the **published EPSG/PROJ transformation parameters** as the
test oracle. Tolerances below are the maximum acceptable horizontal divergence between
Honua's selected-pipeline output and the EPSG/PROJ reference for the listed pair. The
tests are structured so an ArcGIS-generated golden coordinate set can be dropped in
later (a fixture with documented provenance) without restructuring.

| From | To | Esri default | WKID | EPSG op | Tolerance (horizontal) | Notes |
|---|---|---|---|---:|---:|---|
| NAD83 (4269) | WGS84 (4326) | NAD_1983_To_WGS_1984_1 | 108001 | 1188 | 1e-9 deg | EPSG 1188 is a null transformation; pipeline is `+proj=noop` (exact identity, PROJ-version stable). Validated against the runtime. |
| NAD83(HARN) (4152) | WGS84 (4326) | NAD_1983_HARN_To_WGS_1984_1 | — | 1580 | 1e-9 deg | EPSG 1580 is a null transformation (`+proj=noop`). Seeded under #1501; validated against the runtime in both directions. No distinct Esri WKID — ArcGIS applies no geographic transformation by default for this pair. |
| NAD83(NSRS2007) (4759) | WGS84 (4326) | NAD_1983_NSRS2007_To_WGS_1984_1 | — | 15931 | 1e-9 deg | EPSG 15931 null transformation (`+proj=noop`). Seeded under #1501; runtime-validated. |
| NAD83(2011) (6318) | WGS84 (4326) | NAD_1983_2011_To_WGS_1984_1 | — | 9774 | 1e-9 deg | EPSG 9774 null transformation (`+proj=noop`). Seeded under #1501; runtime-validated. |
| NAD27 (4267) | NAD83 (4269) | NAD_1927_To_NAD_1983_NADCON | 1241 | 1241 | 5e-6 deg | The no-envelope default is CONUS `us_noaa_conus.tif`. An envelope contained in a catalog area selects that grid (Alaska `us_noaa_alaska.tif`, Canada `ca_nrc_ntv2_0.tif`). Canada has explicit priority over overlapping CONUS/Alaska bounds only when the entire envelope fits one parent grid coverage region. No match or an equal-priority ambiguity uses 2-argument `ST_Transform`. Wrapped Alaska bounds contain only their antimeridian arc. |

Tolerances are conservative upper bounds for the *selection* test: they prove Honua
applies the correct pipeline, not that PROJ and EPSG agree to sub-millimeter. Tighten
them when an ArcGIS golden set is added.

### Seeded vs deferred pairs

Only pipelines verified against the PostGIS/PROJ runtime are seeded in the table.

Seeded under #1501: the WGS84-realization pairs **NAD83(HARN) → WGS84** (EPSG 1580),
**NAD83(NSRS2007) → WGS84** (EPSG 15931), and **NAD83(2011) → WGS84** (EPSG 9774). EPSG
defines all three as **null transformations** (the datums are coincident at the meter
level, which is exactly the ArcGIS default of applying no geographic transformation). They
are expressed as the exact-identity `+proj=noop` and validated against the runtime in both
directions (`DatumTransformationParityTests.TransformPoint_Wgs84RealizationNullPair_SeededPipelineApplies`).
The all-zero geocentric-translation Helmert parameters were confirmed against the runtime
`proj.db` `helmert_transformation` table (codes 1188/1580/9774/15931).

#### Applying an explicit operation (#4075)

PostGIS 3.4 and later provide
[`ST_TransformPipeline`](https://postgis.net/docs/ST_TransformPipeline.html).
`DatumTransformSql` uses this function for non-identity selections, orienting the
pipeline for forward or inverse execution. The text argument of `ST_Transform`
is a CRS definition and cannot reliably execute an operation pipeline. An exact
`+proj=noop` selection uses `ST_SetSRID` to retain all ordinates.

`DatumTransformPipelineExecutionTests` executes an affine fixture on real PostGIS.
Its independently calculated forward result adds `(2,-3,5)` to XYZ and its inverse
subtracts those offsets; both retain M=7 and the target SRID. The former SQL fails
these assertions. Grid availability remains a deployment prerequisite for a
selected grid operation; the helper does not substitute an approximation.

The GeometryServer default NAD27/NAD83 path uses SRID-only projection.
`GeometryServiceProjectTests` checks both directions over HTTP in digest-pinned
PostGIS 18 fixtures, with and without the pinned NOAA NADCON grid. Each profile
has an isolated PROJ data directory and disables network grid downloads.
Independent pyproj references cover the grid-free CONUS Helmert operation and
the pinned NADCON operation. Every case asserts XY within `2e-9` degrees,
preserved Z/M, geometry type, and the destination WKID. CI's shared PostGIS 16
database includes a legacy `conus` grid; its default operation must not be
compared with a grid-free reference.

## PROJ grid-data provisioning (#1501)

Grid-based pipelines (NADCON/NTv2/GEOID) depend on PROJ grid files that do **not** ship in
the base `postgis/postgis:*` PROJ data path. The catalog records each pipeline's
`requiredGrids` with the canonical PROJ 9.x grid filename (resolved via `projinfo -o PROJ`).

Runtime observations on the base test image (`postgis/postgis:18-3.6`, PROJ 9.6.0,
`PROJ_NETWORK=OFF`):

- The base image ships only the minimal Debian `libproj-data` subset — the legacy NTv1
  `nad27`/`nad83` ASCII tables and a handful of `.gsb` grids. The canonical modern `.tif`
  grids the EPSG/PROJ pipelines reference (`us_noaa_conus.tif`, the `us_noaa_nadcon5_*`
  realization chain, `ca_nrc_ntv2_0.tif`, `us_noaa_g2018u0.tif` for GEOID18) are **absent**.
- The **2-argument default** `ST_Transform(geom, 4269)` (input SRID 4267) *does* resolve a NAD27→NAD83
  datum shift on the base image, but via a **lower-accuracy** bundled fallback operation —
  it differs from the canonical-grid result at the ~1e-4 deg level. So NAD27 import/query
  reprojection is not silently identity, but it is not full-accuracy NADCON either.
- The seeded NADCON pipeline can be forced through `ST_TransformPipeline` when
  its grid is installed. Without the grid, explicit execution fails; SRID-only
  operation selection remains distinct.

### The `docker/proj-grids` image

[`docker/proj-grids`](../../../docker/proj-grids/README.md) provisions the canonical
PROJ grids into `/usr/share/proj`. The default customer Compose deployment builds
it on `pgrouting/pgrouting:17-3.5-3.7.3`, preserving PostgreSQL 17 and existing data
volumes. The customer archive includes the complete build context. Grid downloads
happen at image build time; `PROJ_NETWORK=OFF` prevents runtime network dependence.
The standalone diagnostic composition retains its separate pinned PostGIS base.

`DatumGridProvisioningTests` builds the customer image and starts an isolated
container as part of the ordinary Postgres integration tests. It requires no
`HONUA_PROJ_GRID_TEST` opt-in. It executes selected CONUS, Alaska and Canadian
pipelines through the shared SQL helper. Grid-free fixtures separately retain
the explicit missing-grid failure contract.

The Canadian operation is [EPSG 1313](https://epsg.org/transformation_1313/NAD27-to-NAD83-4.html)
with `ca_nrc_ntv2_0.tif`; Puerto Rico uses
[EPSG 1461](https://epsg.org/transformation_1461/Puerto-Rico-to-NAD83-1.html)
with `us_noaa_prvi.tif`. `projinfo EPSG:1313 -k operation -o PROJ` and
`projinfo EPSG:1461 -k operation -o PROJ` expose the canonical PROJ pipelines.
Explicit client WKIDs continue to select their named operation independently of
area priority. Automatic Canadian selection additionally requires containment in one of the
four parent regions encoded in the canonical TIFF; the operation's rectangular area
of use includes a southern central coverage gap. Envelopes crossing parent-region
boundaries conservatively fall back to another contained area or `ST_Transform`.

The Alaska and Canadian integration cases also compare against independent `cct`
outputs within **0.01 m horizontal distance** on the NAD83 ellipsoid. These
references were generated on 13 November 2026 using Debian Bookworm's `proj-bin`
and the canonical grids downloaded by the image build, without loading the Honua
catalog or executing its SQL helper:

| Grid | NAD27 input (longitude, latitude) | NAD83 reference (longitude, latitude) |
|---|---|---|
| `us_noaa_alaska.tif` | `-149.9, 61.2` | `-149.902204940219, 61.199447934275` |
| `ca_nrc_ntv2_0.tif` | `-79.4, 43.7` | `-79.399801388899, 43.700056900002` |

Reproduce each reference with the corresponding input and grid:

```bash
printf '%s\n' '-149.9 61.2 0' | cct -d 12 \
  +proj=pipeline \
  +step +proj=unitconvert +xy_in=deg +xy_out=rad \
  +step +proj=hgridshift +grids=us_noaa_alaska.tif \
  +step +proj=unitconvert +xy_in=rad +xy_out=deg
```

## Import / reprojection path

The file/migration **import** reprojection path (`StreamingFileImportService`) now honors
the same auditable Esri-default selection as the query path (#1501). When a feature is
reprojected on import (`sourceSrid → targetSrid`), the service resolves the catalog's
Esri-default pipeline via `IDatumTransformationCatalog.TryGetDefault` and applies it
through `ST_TransformPipeline` inside `honua.insert_import_feature` (migration 123).
The text argument of `ST_Transform` is a source CRS and cannot execute an operation
pipeline. An exact `+proj=noop` selection is rewritten to `ST_SetSRID`, matching
`DatumTransformSql`.

Details and guarantees:

- The reprojection executes server-side inside `honua.insert_import_feature`. Migration
  `053_AddImportDatumTransformation.sql` adds an additive 7-argument overload that takes
  the resolved PROJ pipeline; the legacy 6-argument overload is unchanged, and the import
  service calls the 7-argument form **only** when a pipeline is actually resolved. Imports
  with no curated default for the pair therefore keep PROJ's default (2-argument) behavior
  byte-for-byte.
- The pipeline is resolved for the request-level `(sourceSrid → targetSrid)` pair and is
  applied per row **only** when the row's source SRID matches that pair. Rows carrying a
  different per-feature SRID (e.g. mixed-CRS FileGDB layers) fall back to PROJ's default
  pipeline; per-feature pipeline selection for heterogeneous-CRS imports is a follow-up.
- Reverse selections are oriented with `ProjPipelineInverter` before they are bound,
  the same direction fix the query path applies. A reverse NADCON import therefore runs
  the inverse grid shift rather than PROJ's default pipeline.
- Grid-gated selections (NADCON/NTv2/GEOID) follow the same explicit-failure contract as
  the query path: a missing grid surfaces as a PostGIS error (mapped to the shared problem
  helper) rather than a silent Helmert approximation. The default customer PostGIS image
  provisions the grids as described above.
- The legacy bulk helpers in `BulkImportExtensions` (`bulk_insert_import_features`, COPY)
  are not on the live import path (no production callers) and are not yet routed; if they
  are ever re-wired they must adopt the same overload.

## Vertical datum (Phase 4 — minimal)

Full vertical datum transformation (NAVD88 ↔ ellipsoidal via geoid models) is **not**
implemented. The query path no longer silently ignores a requested vertical CS: when
`outSR` carries a `vcsWkid`/`latestVcsWkid`, the FeatureServer query returns an explicit
"Unsupported vertical transformation" error. Full geoid support (GEOID18 grids, COMPOUND
CRS handling) is a documented follow-up.
