# Datum / vertical transformation fidelity (Esri-default parity)

Honua reprojects geometry with PostGIS' embedded PROJ engine. The accuracy gap this
document addresses is **pipeline selection**, not engine capability: a bare
`ST_Transform(geom, toSrid)` lets PROJ choose its own "best available" pipeline, which
does not always match the geographic (datum) transformation ArcGIS selects by default.
That divergence is small but real — roughly 1–2 m for NAD83↔WGS84 and larger for NAD27.

To close the gap, Honua models the Esri default geotransformations as an auditable
data table and drives PostGIS' **3-argument** `ST_Transform(geom, '<proj-pipeline>', toSrid)`
so the selected (Esri-parity) pipeline is used.

## How it works

- `IDatumTransformationCatalog` (`Honua.Core.Features.Infrastructure.Crs`) resolves a
  `(fromSrid, toSrid)` reprojection to a `DatumTransformationSelection` carrying the
  Esri WKID, EPSG operation code, and explicit PROJ pipeline string.
- `EsriDatumTransformationCatalog` is backed by the embedded table
  `src/Honua.Core/Features/Infrastructure/Crs/Resources/esri-default-datum-transformations.json`.
- The GeoServices FeatureServer query handler:
  - **Honors** a client-supplied `datumTransformation` (bare WKID or
    `{"geoTransforms":[{"wkid":...,"transformForward":...}]}`) by resolving it against
    the catalog. An unknown or inapplicable WKID returns an **explicit** Esri-style
    error — it is never silently substituted.
  - **Defaults** to the catalog's Esri default for the layerSR→outSR pair when the
    client sends no `datumTransformation`.
  - Surfaces support via `supportsQueryWithDatumTransformation` in layer metadata.
- All output-CRS `ST_Transform` SQL call sites route through the single
  `DatumTransformSql.BuildTransformExpression` chokepoint so the 2-arg vs 3-arg choice
  stays consistent and cannot drift.

## Tolerance table

Parity is asserted against the **published EPSG/PROJ transformation parameters** as the
test oracle. Tolerances below are the maximum acceptable horizontal divergence between
Honua's selected-pipeline output and the EPSG/PROJ reference for the listed pair. The
tests are structured so an ArcGIS-generated golden coordinate set can be dropped in
later (a fixture with documented provenance) without restructuring.

| From | To | Esri default | WKID | EPSG op | Tolerance (horizontal) | Notes |
|---|---|---|---|---:|---:|---|
| NAD83 (4269) | WGS84 (4326) | NAD_1983_To_WGS_1984_1 | 108001 | 1188 | 0.01 m | EPSG 1188 is a null transformation; pipeline is `+proj=noop` (exact identity, PROJ-version stable). Validated against the runtime. |
| NAD27 (4267) | NAD83 (4269) | NAD_1927_To_NAD_1983_NADCON | 1241 | 1241 | 0.3 km* | Grid-based (NADCON); requires `us_noaa_conus.tif`. *When the grid is absent the transform fails explicitly (null), per the grid-data follow-up below. |

Tolerances are conservative upper bounds for the *selection* test: they prove Honua
applies the correct pipeline, not that PROJ and EPSG agree to sub-millimeter. Tighten
them when an ArcGIS golden set is added.

### Seeded vs deferred pairs

Only pipelines verified against the PostGIS/PROJ runtime are seeded in the table.
Additional Esri-default pairs that use multi-step Helmert pipelines (for example
`NAD_1983_To_NAD_1983_2011_1` / EPSG 1311, `WGS_1984_(ITRF00)_To_NAD_1983_2011` /
EPSG 8259, and HARN realizations) are a **documented follow-up**: each Helmert PROJ
pipeline string must be generated from the EPSG operation via `projinfo` and validated
against the runtime before seeding, so the catalog never ships a pipeline string that
PostGIS rejects (which would surface as a null/error result rather than a silent wrong
answer). Until seeded, those pairs fall through to PROJ's default pipeline (no regression
versus prior behavior) and a client-requested WKID for them returns an explicit
"unsupported" error.

## PROJ grid-data requirement (follow-up)

Grid-based pipelines (NADCON/NTv2/GEOID) depend on PROJ grid files that may not ship in
the PostGIS image's default PROJ data path. The catalog records each pipeline's
`requiredGrids`; when a required grid is absent the runtime must **fail explicitly**
rather than degrade to a Helmert approximation.

This PR does **not** modify the Docker image. Provisioning the PROJ grid data
(e.g. `us_noaa_conus.tif` for NAD27↔NAD83 NADCON, and GEOID grids for vertical work) in
the PostGIS image is a documented follow-up. Until then, grid-backed selections that hit
a missing grid surface a PostGIS error mapped to the shared problem helper.

## Import / reprojection path (follow-up)

The query/output path selects and applies the Esri-default transformation. The file/
migration **import** reprojection path (`StreamingFileImportService`) currently relies on
PostGIS' default pipeline when the source datum differs from the storage datum.
Recording and applying the catalog-selected transformation on import (so stored geometry
matches Esri-default reprojection) is a documented follow-up; it does not route through
the shared `DatumTransformSql` chokepoint yet.

## Vertical datum (Phase 4 — minimal)

Full vertical datum transformation (NAVD88 ↔ ellipsoidal via geoid models) is **not**
implemented. The query path no longer silently ignores a requested vertical CS: when
`outSR` carries a `vcsWkid`/`latestVcsWkid`, the FeatureServer query returns an explicit
"Unsupported vertical transformation" error. Full geoid support (GEOID18 grids, COMPOUND
CRS handling) is a documented follow-up.
