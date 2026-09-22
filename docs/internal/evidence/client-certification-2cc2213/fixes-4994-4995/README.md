# Features client regression evidence: #4994 and #4995

These fixes restore the 2026.1 release promise that declared OGC API Features
workflows work in the bounded desktop-client roster: individual features must
retain their coordinates, and supported filters must be discoverable and run
on the server. Both issues remain in ruling B's must-fix-before-cut scope.

## Candidate and independent oracle

- Baseline: `ghcr.io/honua-io/honua-server:nightly-2cc2213`, image
  `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51`.
- Fixed source: `f1592ad57c` (subsequent commits contain evidence/harness changes only).
- Fixed local candidate: `honua-server:ogc4994-fixed`, image
  `sha256:f472c668ae722b8e9e04ca213c7677d6066fa579bb1bd7db0a007f5078b0cef3`.
  Built from the actual Debug publish output with `PublishAot=false`,
  `UseAppHost=false`, `--self-contained false`; tested after trimming/publishing.
  This is local fix validation, not an official full-roster release attestation.
- GDAL/OGR 3.8.4: `ghcr.io/osgeo/gdal:ubuntu-small-3.8.4`, digest
  `sha256:60d3bc2f8b09ca1a7ef2db0239699b2c03713aa02be6e525e731c0020bbb10a4`.
- QGIS 3.44.13: `qgis/qgis:3.44.13`, digest
  `sha256:59e160b2ea3f881be039ed49936c42e75e8f336688951f22147676db743f4e47`.
- Fixture: `tests/seed/client-compat-v1.sql`, SHA-256
  `7cf9f0e0c2ba2de6aadef788b86e0371762627dfdb4fe5f1e4088cc993ba62d4`.
  Literal expectations are authored independently: feature 3 is gamma at
  `(-122.46, 37.73)`; active IDs are `[1,3,5,7,9]`; feature 10 has null geometry.
  The .NET fixture independently checks all supported filter encodings against
  `tests/seed/server.yaml`, including its null-geometry feature.

## Results

| Check | Baseline | Fixed candidate |
| --- | --- | --- |
| GDAL single item | `(37.73,-122.46)`, wrong axes | `(-122.46,37.73)` |
| GDAL queryables + filter pushdown | no queryables/filter request | queryables GET and `filter-lang=cql-text`, exact five IDs |
| QGIS queryables + filter pushdown | no queryables/filter request | queryables GET and `filter-lang=cql2-text`, exact five names |
| GDAL ITEM roster | original issue failure | all five governed facets pass |
| GDAL QUERYABLES roster | original issue failure | all five governed facets pass |
| QGIS QUERYABLES roster | original issue failure | all five governed facets pass |

Raw before/after logs and cell observations are adjacent. QGIS's OPTIONS probes
receive 405; the actual queryables and filtered GET requests return 200. No
mutation is performed by these probes. No acceptance criterion is released.

## Reproduction

Use an isolated `docker/client-compat/compose.yml` project with
`PUBLIC_BASE_URL=http://honua:5000`, run its seed, and replace only its Honua image
with the published fix candidate. The legacy seed recreates raster tables after
migration journaling; before current-trunk startup, restore migration 055's two
`SET STORAGE EXTERNAL` statements in that isolated database. Flush the isolated
Redis DB when replacing the baseline image, so cached conformance/OpenAPI
responses cannot answer fixed-candidate requests. These are test-environment
steps, not application migrations or changes to shared stacks.

Mount `certification/bounded-roster/regressions` at `/regressions:ro`, join the
candidate's Docker network, and run:

```sh
# In the pinned GDAL image:
python3 /regressions/features_clients.py gdal
# In the pinned QGIS image (QT_QPA_PLATFORM=offscreen):
python3 /regressions/features_clients.py qgis
```

The original bounded harness is from
`bdb06a126201c7d18c99fa82ed9f0347d28e0ed5` on
`fix/3434-bounded-roster-2cc2213`. Extract its `certification/bounded-roster`,
`certification/client-protocol-requirements.v1.json`, and `tests/python/shared`.
Apply `certification/bounded-roster/regressions/roster-client-metadata.patch`
with `git apply --unidiff-zero`.
The patch preserves the original assertions' functional coverage:

- Select EPSG:4326 explicitly for the latitude-first CRS facet and set GDAL's
  traditional GIS axis mapping strategy. GDAL 3.8.4's default `IsSame` ignores
  geographic axis order; explicit selection alone chooses the first equivalent
  URI (now correctly CRS84). All original coordinate, bbox, and projected-CRS
  assertions remain unchanged. The focused script separately asserts the
  unconfigured default CRS84 single-item coordinates.
- Assert QGIS's actual `QVariant.LongLong` and `QVariant.DateTime` field types.
  Its OAPIF provider populates these enums but leaves `typeName()` empty.
  The checks still require integer counts, datetime timestamps, Point geometry,
  and GeoJSON response media types.

With the harness on `PYTHONPATH`, run `gdal_ogc_cells.features_item()`,
`gdal_ogc_cells.features_queryables()`, and `qgis_cells.oapif_queryables()` in the
corresponding pinned containers. The observations include positive, negative,
authentication, CRS/axis, and media/schema checks. These three passing cells do
not assert that the other roster cells or the official release candidate have
been recertified.
