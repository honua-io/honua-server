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
| NAD83 (4269) | WGS84 (4326) | NAD_1983_To_WGS_1984_1 | 108001 | 1188 | 1e-9 deg | EPSG 1188 is a null transformation; pipeline is `+proj=noop` (exact identity, PROJ-version stable). Validated against the runtime. |
| NAD83(HARN) (4152) | WGS84 (4326) | NAD_1983_HARN_To_WGS_1984_1 | — | 1580 | 1e-9 deg | EPSG 1580 is a null transformation (`+proj=noop`). Seeded under #1501; validated against the runtime in both directions. No distinct Esri WKID — ArcGIS applies no geographic transformation by default for this pair. |
| NAD83(NSRS2007) (4759) | WGS84 (4326) | NAD_1983_NSRS2007_To_WGS_1984_1 | — | 15931 | 1e-9 deg | EPSG 15931 null transformation (`+proj=noop`). Seeded under #1501; runtime-validated. |
| NAD83(2011) (6318) | WGS84 (4326) | NAD_1983_2011_To_WGS_1984_1 | — | 9774 | 1e-9 deg | EPSG 9774 null transformation (`+proj=noop`). Seeded under #1501; runtime-validated. |
| NAD27 (4267) | NAD83 (4269) | NAD_1927_To_NAD_1983_NADCON | 1241 | 1241 | 0.3 km* | Grid-based (NADCON); requires `us_noaa_conus.tif`. *See the PROJ pipeline-application constraint below — a `+proj=pipeline` string cannot be forced through PostGIS' text overload, so the seeded NADCON pipeline does not apply; the explicit-failure (null) contract holds, and the 2-argument default path already resolves PROJ's NADCON-equivalent shift. |

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

#### PROJ pipeline-application constraint (deferred non-null pairs)

The remaining Esri-default pairs that resolve to a **non-identity** operation — the
rotation-bearing Helmert pairs (e.g. `WGS_1984_(ITRF00)_To_NAD_1983` / EPSG 108190 family,
`NAD_1983_HARN_To_WGS_1984_2` / EPSG 1901) and the grid-based NADCON/NTv2 pairs — remain a
**documented follow-up**, but **not** because the PROJ pipeline string is unknown. The
blocker is a PostGIS limitation: `ST_Transform`'s text overload is
`ST_Transform(geom, from_proj text, to_srid int)`, where the middle argument is the
**source CRS**, not a coordinate-operation pipeline. PostGIS on the current runtime
(PostGIS 18-3.6 / PROJ 9.6) exposes **no overload that injects a `+proj=pipeline` string**;
a pipeline string passed there fails to parse as a CRS. Only proj strings that parse as a
CRS apply — and `+proj=noop`, which behaves as exact identity, which is why the null pairs
can be seeded today.

Consequently the `DatumTransformSql` 3-argument chokepoint can force the **identity** Esri
default but cannot yet force a specific **non-identity** operation. Until a PostGIS
pipeline-application mechanism is available (e.g. a custom `spatial_ref_sys` operation
registration, a `cs2cs`-style sidecar, or a PostGIS overload that accepts a coordinate
operation), those pairs continue to use PROJ's **default** operation selection on the
2-argument path. PROJ's default already follows EPSG accuracy ranking, which matches Esri's
default selection for these CONUS pairs (no regression versus prior behavior), and a
client-requested WKID for a non-seeded pair returns an explicit "unsupported" error rather
than a silent substitute.

## PROJ grid-data requirement (follow-up)

Grid-based pipelines (NADCON/NTv2/GEOID) depend on PROJ grid files that may not ship in
the PostGIS image's default PROJ data path. The catalog records each pipeline's
`requiredGrids`; when a required grid is absent the runtime must **fail explicitly**
rather than degrade to a Helmert approximation.

Runtime observations on the test image (`postgis/postgis:18-3.6`, PROJ 9.6.0,
`NETWORK_ENABLED=OFF`):

- The seeded NADCON pipeline string is a `+proj=pipeline +step +proj=hgridshift ...`
  expression, which — per the PROJ pipeline-application constraint above — **cannot be
  forced through PostGIS' `ST_Transform` text overload**, so the explicit-grid path always
  fails to apply on this runtime regardless of whether the grid is present. The
  explicit-failure (null) contract therefore holds.
- The **2-argument default** `ST_Transform(geom, 4267, 4269)` *does* resolve a NAD27→NAD83
  datum shift on this image (PROJ selects its best-available NADCON-equivalent operation
  from the data bundled with PROJ 9.6), so import/query reprojection of NAD27 data is not
  silently identity.

This PR does **not** modify the production Docker image. Provisioning explicit PROJ grid
data (e.g. `us_noaa_conus.tif` for high-accuracy NADCON, NTv2 `.gsb` files, and GEOID
grids for vertical work) into the image — and a pipeline-application mechanism to actually
force them — is a documented follow-up. Until then, grid-backed *selections* that hit a
missing grid surface a PostGIS error mapped to the shared problem helper, and the
2-argument default path provides PROJ's best-available operation.

## Import / reprojection path

The file/migration **import** reprojection path (`StreamingFileImportService`) now honors
the same auditable Esri-default selection as the query path (#1501). When a feature is
reprojected on import (`sourceSrid → targetSrid`), the service resolves the catalog's
Esri-default pipeline via `IDatumTransformationCatalog.TryGetDefault` and applies it
through the explicit 3-argument `ST_Transform(geom, '<pipeline>', toSrid)` form of
`honua.insert_import_feature` — the same shape `DatumTransformSql.BuildTransformExpression`
emits for the query path.

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
- Only **forward** selections are applied. The catalog synthesizes reverse directions with
  `TransformForward = false` but keeps the forward pipeline; applying that forward pipeline to
  reverse-direction input (e.g. a NAD27→NAD83 NADCON shift on NAD83 coordinates) would corrupt
  the result, so reverse-direction imports fall back to PROJ's default path until inverse
  pipelines are emitted.
- Grid-gated selections (NADCON/NTv2/GEOID) follow the same explicit-failure contract as
  the query path: a missing grid surfaces as a PostGIS error (mapped to the shared problem
  helper) rather than a silent Helmert approximation. Provisioning the grid data in the
  PostGIS image remains the separate follow-up above.
- The legacy bulk helpers in `BulkImportExtensions` (`bulk_insert_import_features`, COPY)
  are not on the live import path (no production callers) and are not yet routed; if they
  are ever re-wired they must adopt the same overload.

## Vertical datum (Phase 4 — minimal)

Full vertical datum transformation (NAVD88 ↔ ellipsoidal via geoid models) is **not**
implemented. The query path no longer silently ignores a requested vertical CS: when
`outSR` carries a `vcsWkid`/`latestVcsWkid`, the FeatureServer query returns an explicit
"Unsupported vertical transformation" error. Full geoid support (GEOID18 grids, COMPOUND
CRS handling) is a documented follow-up.
