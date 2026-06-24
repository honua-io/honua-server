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
| NAD27 (4267) | NAD83 (4269) | NAD_1927_To_NAD_1983_NADCON | 1241 | 1241 | 5e-6 deg† | Grid-based (NADCON); requires `us_noaa_conus.tif`. *See the PROJ pipeline-application constraint below — a `+proj=pipeline` string cannot be forced through PostGIS' text overload, so the seeded NADCON pipeline does not apply via the 3-arg form; the 2-argument default path resolves the NADCON shift, and its accuracy depends on whether the canonical grid is provisioned. †Tolerance pins the value measured on the `docker/proj-grids` image (canonical `us_noaa_conus.tif` baked in): NAD27 (-100, 40) → NAD83 ≈ (-100.0004056, 40.0000058), a ~36 m CONUS shift; the base image's minimal `libproj-data` subset gives a lower-accuracy fallback differing at the ~1e-4 deg level. |

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
- The **2-argument default** `ST_Transform(geom, 4267, 4269)` *does* resolve a NAD27→NAD83
  datum shift on the base image, but via a **lower-accuracy** bundled fallback operation —
  it differs from the canonical-grid result at the ~1e-4 deg level. So NAD27 import/query
  reprojection is not silently identity, but it is not full-accuracy NADCON either.
- The seeded NADCON `+proj=pipeline +step +proj=hgridshift ...` string still **cannot be
  forced through PostGIS' `ST_Transform` text overload** (see the constraint above), so the
  3-argument explicit-grid form does not apply on this runtime; the grid is exercised
  through the **2-argument default** path PROJ selects from the available grid data.

### The `docker/proj-grids` image

[`docker/proj-grids`](../../../docker/proj-grids/README.md) provisions the canonical PROJ
grids into the PostGIS image's PROJ data directory (`/usr/share/proj`). It extends
`postgis/postgis:18-3.6` and bakes in exactly the grids listed in
[`docker/proj-grids/grids.txt`](../../../docker/proj-grids/grids.txt) (kept in sync with the
catalog's `requiredGrids`), fetched from the PROJ CDN at build time. With the grids present,
PROJ's 2-argument default resolves the **high-accuracy** NADCON operation, so the shared
`DatumTransformSql` chokepoint (query + import reprojection) produces the canonical grid
shift instead of the fallback. Measured: NAD27 (-100, 40) → NAD83 ≈ (-100.0004056, 40.0000058).

This image is **isolated and opt-in**: nothing in the default build / Fast-tier test / CITE
path builds or pulls it, and the base PostGIS tag used elsewhere is unchanged. Wiring it (or
its baked grids) into the integration/CITE compositions — which would bump those image tags —
is a deliberate follow-up. The gated `DatumGridProvisioningTests` (env `HONUA_PROJ_GRID_TEST`,
pointed at this image via `HONUA_TEST_DB_URL`) prove the grid-gated transform resolves
in-tolerance when the grids are present; `DatumTransformationParityTests` continue to assert
the default-path / explicit-failure contract on the grid-less fixture.

A pipeline-application mechanism that could force a *specific non-default* grid/Helmert
operation through PostGIS (rather than relying on PROJ's default operation selection) remains
the separate follow-up described under the PROJ pipeline-application constraint above.

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
