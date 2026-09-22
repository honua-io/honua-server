# Bounded 2026.1 external-client roster — candidate proof on `nightly-2cc2213`

Issue: honua-io/honua-server#3434. Operator ruling A (2026-09-16): proofs run on the newest
imaged trunk nightly rather than the frozen release pin (`87966c3`); no release cut exists,
so this is a pre-cut proof and records no cut ID.

**Verdict: not certified.** Of the 59 governed cells, **43 pass**, **15 fail** and **1 is
blocked** (no receipt possible). The release-mode gate stays red, as it must. Every failing
or blocked cell names a filed blocker below.

## Candidate

| | |
| --- | --- |
| Image | `ghcr.io/honua-io/honua-server:nightly-2cc2213` |
| Index digest | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` |
| Source SHA | `2cc221388ea47d78c29e79eaee62737e4c792351` (image label `org.opencontainers.image.revision`) |
| Image created | `2026-09-16T10:25:15.432Z` (used as `--cut-at`) |
| Build | nightly-container-build run 35084811526 (NativeAOT) |
| Server version | `2026.1.1`, metadata `metadata.honua.io/v2alpha1` / schema `2.0.0-alpha.1` (`/api/v1/admin/version`) |
| Target | `local-docker` (one exact-image Docker stack; the candidate was pulled by digest and never rebuilt or restarted after seeding) |

A newer imaged nightly (`d1fc139`) completed after this run started; the dispatch pins the
newest nightly *at start*.

## Producer run

| | |
| --- | --- |
| Harness | `certification/bounded-roster/` at `786b664f916b5c98fb2393555bf55801dc5f16e1` (`producer_source_sha` in every receipt) |
| Run | `roster-20260916T213457Z-2cc2213`, 2026-09-16 21:34:57Z – 21:51:40Z |
| Command | `certification/bounded-roster/run-roster.sh --tag nightly-2cc2213 --expect-digest sha256:61e06ef3… --evidence docs/internal/evidence/client-certification-2cc2213 --run-root <real disk>` |
| Denominator | `certification/client-protocol-requirements.v1.json` revision `2026-09-16-bounded-roster.3`, projected from honua-release `97e34bdb284f57ecea1de577805311c78cb52941` (denominator `2026-09-13-complete.12`, honua-release#347) |

## Fixture, configuration and auth profile

The receipts echo the governed revision strings exactly, because they are the join keys
(`fixture_revision` `docker/cng/seed.sql@2cc2213…`, `server_config_revision` and
`auth_policy_revision` per row). What was actually applied is digested in
[`run/fixture.json`](run/fixture.json):

| Set | sha256 (set) | Members |
| --- | --- | --- |
| Fixture | `373c9e9c19630d35f76bfea62007bcac3eb68fad880efcc32c1c545fea6fec84` | client-compat seeds (`tests/seed/client-compat-v1.sql`, `browser-compat.yaml`, `client-compat-auth-wave1.yaml`, `portal-compat.yaml`), `docker/cng/seed.sql`, `roster-raster-fixture.sql`, `apply_fixture.py` |
| Server config | `b93d37649eb33faa96c5d01f91a6f99a151d311cb28d86145fcf8e19cddd0a99` | `docker/client-compat/compose.yml`, `certification/bounded-roster/compose.candidate.yml` |
| Auth profile | `7ae3e6d6a24e9cf767e8c7c467f87da85f274aba259d1ef052eeb68fc65d4958` | `tests/python/shared/cert_auth.py`, `certification/bounded-roster/lib/rosterenv.py` |

The frozen #3393 manifest (`docs/gis/data/client-certification-fixture.v1.json`) verifies clean
at the producer commit (`python3 scripts/certification/verify-fixture-manifest.py` → `OK`):

| #3393 revision | Digest |
| --- | --- |
| `fixtureRevision` | `sha256:01cb0f301f987bf98d9420a6bd9637de5582e8a7eafb04f56ba86b2888dbeb22` |
| `serverConfigRevision` | `sha256:d4b2189558e492204909a75ccc71054741042fa7974d600e82a7a0ee0213435a` |
| `authPolicyRevision` | `sha256:9068f9d255f917b14ba5cff7c9a9defc268f69892e7605923f9d3f5dc3f5fea9` (policy `client-compat-auth-v1`) |

Every manifest fixture input was applied unchanged by the client-compat seed service. The run
extends that frozen set with `docker/cng/seed.sql` (the governed label) and the roster raster
fixture, which the manifest does not yet cover (its expansion is tracked by #3435).

Authored cloud objects: COG `cog/roster-3857.tif` sha256
`8f3d72c953f432497cd8a144c671cf3a097f901e3825dc759b64170baddd286e` and a 14-object Zarr v2
datacube ([`run/fixture-artifacts.json`](run/fixture-artifacts.json)). The candidate accepted
all four registrations but none of its four `/refresh` scans returned within 90 s (#4998).

The governed rows all name `docker/cng/seed.sql` as their fixture, but that seed alone
publishes no protected mirrors, rasters, styles or tiles; the applied set above is the
smallest that gives every governed facet real data. honua-release owns the label.

## Clients

| Lane | Client (exact) | Image | Base image digest |
| --- | --- | --- | --- |
| `desktop-qgis` | QGIS `3.44.13-Solothurn` (PyQGIS, headless) | `sha256:af0e7520ab27…` | `qgis/qgis@sha256:59e160b2ea3f881be039ed49936c42e75e8f336688951f22147676db743f4e47` |
| `gdal`, `gdal-cog`, `gdal-flatgeobuf` | GDAL `3.8.4, released 2024/02/08` | `sha256:1952c4a24a44…` | `ghcr.io/osgeo/gdal@sha256:60d3bc2f8b09ca1a7ef2db0239699b2c03713aa02be6e525e731c0020bbb10a4` |
| `gdal` (raster) | GDAL `3.13.3 "Iowa City", released 2026/08/13` | `sha256:87bd1773a401…` | `ghcr.io/osgeo/gdal@sha256:64250faf833c06d4b21afce4c27190039ba7ab58d70f0eebc87cf77d929c0b40` |
| `js-maplibre` | maplibre-gl `5.7.3` (governed `5.7`) and `6.5.0`, Chromium from Playwright 1.59.1 | `sha256:55e42a126c07…` | `mcr.microsoft.com/playwright@sha256:b0ab6f3cb99aa7803adbc14d9027ec1785fc6e433b97e134e0f8fe61683b6b53` |
| `py-owslib`, `py-pystac` | OWSLib `0.36.0`; pystac `1.15.2`, pystac-client `0.9.0` | `sha256:df71bb3df1d4…` | `python@sha256:782412e85d0f0984994c290652577d4018aff08145c85b262bb63dc0c7522254` |

Full image IDs, the pinned MapLibre dist hashes and the Python freeze are in
[`run/lanes.json`](run/lanes.json) and [`run/observations/lanes/`](run/observations/lanes/).

## Cells

| Client | Version | Surface | Operation | Verdict | Failing facets / reason | Blocker |
| --- | --- | --- | --- | --- | --- | --- |
| QGIS | `3.44.13-Solothurn` | `featureserver` | `serve.geoservices-featureserver` | pass | 3 checks across positive, metadata, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `mapserver` | `serve.geoservices-mapserver` | pass | 3 checks across positive, metadata, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-COLLECTION` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-COLLECTIONS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-CONFORMANCE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-ITEM` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-ITEMS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-LANDING` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-QUERYABLES` | **fail** | positive | #4995 |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-FEATURES-TRANSACTIONS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-TILES-LANDING-TILESETS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-OGC-API-TILES-TILE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-WFS-2-0` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-WMS-GETCAPABILITIES-GETMAP-GETFEATUREINF` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc` | `OGC-OP-WMTS-GETCAPABILITIES-GETTILE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc-api-features` | `serve.ogc-api-features` | pass | 7 checks across positive, negative, auth, pagination, limit, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc-api-maps` | `serve.ogc-api-maps` | **fail** | auth, crs-axis, media-schema, positive | #4991 |
| QGIS | `3.44.13-Solothurn` | `ogc-api-styles` | `styling.ogc-api-styles` | **fail** | media-schema, positive | #4993 |
| QGIS | `3.44.13-Solothurn` | `ogc-api-tiles` | `serve.ogc-api-tiles` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `ogc-features` | `serve.ogc-api-features` | pass | 3 checks across positive, metadata, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `wfs` | `serve.wfs` | pass | 3 checks across positive, metadata, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `wms` | `serve.wms` | pass | 3 checks across positive, metadata, media-schema |  |
| QGIS | `3.44.13-Solothurn` | `wmts` | `serve.wmts` | pass | 3 checks across positive, metadata, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-COLLECTION` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-COLLECTIONS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-CONFORMANCE` | **fail** | positive | honua-release#359 |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-ITEM` | **fail** | positive | #4994 |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-ITEMS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-LANDING` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-QUERYABLES` | **fail** | positive | #4995 |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-OGC-API-FEATURES-TRANSACTIONS` | **fail** | positive | honua-release#359 |
| GDAL/OGR | `3.8.4` | `ogc` | `OGC-OP-WFS-2-0` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `ogc-api-features` | `serve.ogc-api-features` | pass | 7 checks across positive, negative, auth, pagination, limit, crs-axis, media-schema |  |
| GDAL/OGR | `3.8.4` | `wfs` | `serve.wfs` | pass | 7 checks across positive, negative, auth, pagination, limit, crs-axis, media-schema |  |
| GDAL | `3.8.4` | `cog` | `dataset-read` | **fail** | crs-axis, metadata, positive | #4998 |
| GDAL | `3.8.4` | `flatgeobuf` | `feature-read` | pass | 3 checks across positive, metadata, crs-axis |  |
| GDAL | `3.14.0` | `geoparquet` | `feature-read` | **blocked** | no receipt: GDAL 3.14.0 is unreleased | honua-release#359 |
| GDAL | `3.8.4` | `ogc` | `OGC-OP-WCS-2-0-COVERAGE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| GDAL | `3.8.4` | `ogc-api-coverages` | `serve.ogc-api-coverages` | **fail** | boundary, crs-axis, media-schema, positive | #4996, honua-release#351 |
| GDAL | `3.13.3` | `raster-cloud-cog-serving` | `raster.cloud-cog-serving` | **fail** | auth, crs-axis, media-schema, metadata, negative, positive, range-efficiency | #4998 |
| GDAL | `3.13.3` | `raster-multidim-coverage` | `raster.multidim-coverage` | **fail** | auth, boundary, crs-axis, media-schema, metadata, positive | #4998 |
| GDAL | `3.8.4` | `wcs` | `serve.wcs` | pass | 6 checks across positive, negative, auth, boundary, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc` | `OGC-OP-OGC-API-TILES-LANDING-TILESETS` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc` | `OGC-OP-OGC-API-TILES-TILE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc` | `OGC-OP-WMS-GETCAPABILITIES-GETMAP-GETFEATUREINF` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc` | `OGC-OP-WMTS-GETCAPABILITIES-GETTILE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc-api-maps` | `serve.ogc-api-maps` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `ogc-api-styles` | `styling.ogc-api-styles` | **fail** | auth, media-schema, positive | #4993 |
| MapLibre GL JS | `5.7` | `ogc-api-tiles` | `serve.ogc-api-tiles` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `6.5.0` | `raster-terrain-rgb` | `raster.terrain-rgb` | pass | 6 checks across positive, negative, auth, boundary, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `wms` | `serve.wms` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| MapLibre GL JS | `5.7` | `wmts` | `serve.wmts` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| OWSLib | `0.36.0` | `ogc` | `OGC-OP-WCS-2-0-COVERAGE` | pass | 5 checks across positive, negative, auth, crs-axis, media-schema |  |
| OWSLib | `0.36.0` | `ogc-api-coverages` | `serve.ogc-api-coverages` | **fail** | boundary, crs-axis | #4996, honua-release#351 |
| OWSLib | `0.36.0` | `ogc-api-edr` | `serve.ogc-api-edr` | **fail** | auth, boundary, crs-axis, media-schema, negative, positive | honua-release#351 |
| OWSLib | `0.36.0` | `ogc-api-processes` | `process.ogc-api-processes` | pass | 5 checks across positive, negative, auth, boundary, media-schema |  |
| OWSLib | `0.36.0` | `ogc-api-records` | `serve.ogc-api-records` | pass | 6 checks across positive, negative, auth, pagination, limit, media-schema |  |
| OWSLib | `0.36.0` | `wcs` | `serve.wcs` | **fail** | boundary | #4997 |
| PySTAC-Client | `pystac=1.15.2;pystac-client=0.9.0` | `stac` | `serve.stac` | pass | 6 checks across positive, negative, auth, pagination, limit, media-schema |  |

## Gate output

Release mode (the gate), over [`receipts/`](receipts/):

```
mode=release requiredCells=59 {'pass': 43, 'fail': 15, 'skip': 1}
  cell-failed: 15
  missing-envelope: 1
  owner honua-io/honua-server: 16
FAIL: the bounded 2026.1 external-client roster is not certified. Missing, skipped, stale, mismatched and source-built required cells fail closed.
```

Contract mode still reports 56 producer blockers because it reads the nightly client-compat
matrix (`tests/baselines/client-compat/expected-pairs.json`), not this harness; the three
`no-candidate` rows are the pairs that matrix emits at the governed versions:

```
mode=contract requiredCells=59 {'pass': 0, 'fail': 0, 'skip': 59}
  no-candidate: 3
  producer-lane-not-emitted: 29
  producer-surface-not-emitted: 27
  owner honua-io/honua-release: 3
  owner honua-io/honua-server: 56
FAIL: the bounded 2026.1 external-client roster is not certified. Missing, skipped, stale, mismatched and source-built required cells fail closed.
```

## Blockers filed

| Issue | Cells |
| --- | --- |
| #4991 OGC API Maps collection resource 404 | QGIS `serve.ogc-api-maps` |
| #4993 advertised stylesheets 404 | QGIS and MapLibre `styling.ogc-api-styles` |
| #4994 storageCrs vs CRS84 single-item axis swap | GDAL/OGR `OGC-API-FEATURES-ITEM` |
| #4995 no Features Part 3 filter/CQL2 conformance | GDAL/OGR and QGIS `OGC-API-FEATURES-QUERYABLES` |
| #4996 coverage link type and `subset` parameter | GDAL and OWSLib `serve.ogc-api-coverages` |
| #4997 WCS out-of-extent subset HTTP 500 | OWSLib `serve.wcs` |
| #4998 S3 range reads hang after HeadObject | GDAL `raster.cloud-cog-serving`, `cog/dataset-read`, `raster.multidim-coverage` |
| honua-release#351 Coverages/EDR demoted to preview in the candidate | OWSLib `serve.ogc-api-edr` (capability disabled by default); both Coverages cells |
| honua-release#359 rows no released GDAL can execute | GDAL `geoparquet/feature-read` (blocked: GDAL 3.14.0 unreleased), GDAL/OGR `CONFORMANCE` and `TRANSACTIONS` (driver never reads conformance; driver is read-only) |

## Acceptance criteria

1. *Frozen profile names every operation and client/version* — met: the mirror carries all 59
   rows with `test_ids`, client and version from denominator `2026-09-13-complete.12`.
2. *Each client runs its public API against the immutable image and fixture digests* — met for
   58 cells; the geoparquet cell cannot run (no GDAL 3.14.0 release, honua-release#359).
3. *Evidence records release/cut, SHA and digest, client version/image, revisions, target,
   operation, timestamps and durable URI* — met: every receipt carries them (`honua_evidence`
   holds the digests, lane images and `durable_uri` under
   `https://github.com/honua-io/honua-server/blob/trunk/docs/internal/evidence/client-certification-2cc2213/receipts/`);
   there is no cut ID because no cut exists (ruling A).
4. *Positive plus invalid-credential, insufficient-role/scope, cross-tenant, pagination/limit,
   metadata and media/schema cases where applicable* — executed per governed `scenario_facets`.
   Auth checks use the governed `anonymous-and-protected` profile: anonymous, wrong API key and
   expired bearer are refused; API key and OIDC bearer are admitted. Insufficient permission is
   exercised where the profile defines it (OGC API Processes execution: a valid bearer without an
   operation grant gets 403). Cross-tenant is not applicable to this profile: its protected
   resources are not tenant-scoped, and a bearer for another tenant is admitted like any
   authenticated principal.
5. *Missing, skipped, stale, mismatched or source-built cells fail the gate* — met and shown:
   the gate is red on 15 failing cells and 1 missing envelope.
6. *Planned and excluded clients remain explicit rows* — unchanged (`delegatedClients` in the
   mirror; ArcGIS stays with honua-esri-compat#74/#75).
7. *No duplicate OWSLib/PySTAC producers* — the nightly matrix lanes (`tests/python/owslib_client`,
   `stac_client`) remain the nightly compatibility suites; this harness is the candidate-bound
   certification producer and emits only governed `client-cert/…` IDs, which the nightly lanes do not.

## How the evidence is built

- Every client request passes through a recording reverse proxy
  (`certification/bounded-roster/proxy/record_wire.py`); [`run/wire.jsonl.gz`](run/wire.jsonl.gz)
  (sha256 of the uncompressed log in [`run/wire.jsonl.sha256`](run/wire.jsonl.sha256)) holds
  method, credential-free URL, credential *scheme*, status, media type and size for each exchange.
  [`wire-join.json`](wire-join.json) records which lines substantiate each cell; a pass with no
  exchange in its window would have been rewritten to fail.
- [`run/observations/`](run/observations/) holds every check each cell ran, with its detail.
- MapLibre pages are served same-origin by that proxy, as a map application behind the same host
  would be; no CORS configuration was added to the candidate.
- No credential, token or key appears in the receipts, observations or wire log.
