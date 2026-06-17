# Open-data area-import provisioner

> Status: **first slice.** A curated open-data source catalog plus one
> end-to-end, area-parameterized import that rides existing Honua Server
> primitives. Esri's "give me a county and load it with their data" workflow,
> repeatable from a single command.

## What this is

Sales and onboarding repeatedly need the same thing: pick a county / state /
city, load it with high-value open government data, and have it served by Honua
in minutes -- without an Esri data credit in sight. This slice delivers:

1. A **structured source catalog** (`data/open-data-catalog/catalog.json`,
   validated by `schema.json`) -- config-as-data, so adding a source is a data
   edit, not a code change.
2. **One end-to-end area-import** (`scripts/provisioner/provision_area.py`):
   given a `--source`, `--product`, and `--area`, it fetches the data, clips it
   to the area, imports it through the existing admin file-import API, publishes
   it as a Honua layer, and (optionally) queues a PMTiles tiling job.
3. This document: how to extend the catalog, and how geocoding/routing plug in
   as future source-products on the same GP-on-Batch engine.

It reuses existing server primitives **only** -- there is no new server code:

| Step | Existing primitive | Endpoint |
|------|--------------------|----------|
| Import | `Honua.Import` file-import (13 formats, job tracking) | `POST /api/v1/admin/import/upload` |
| Publish | `Features/Admin` layer publishing | `POST /api/v1/admin/connections/{id}/layers` |
| Tile | TileCache operations (in-process or GP-on-Batch / Fargate Spot) | `POST /api/v1/admin/tile-operations/jobs` |

Auth is the admin `X-API-Key` everywhere.

> **Why `/upload` and not `/upload-url`?** The server's `upload-url` endpoint
> only accepts **public S3/Azure object hosts** (see
> `ImportSourceUrlValidation.IsSupportedPublicObjectHost`). Open-data hosts like
> `www2.census.gov` and `overpass-api.de` are rejected by design (SSRF
> hardening). So the provisioner downloads + clips locally and pushes the result
> through the multipart `/upload` endpoint -- exactly the fetch → clip → import
> flow this capability needs.

## Quick start

```bash
# 0. deps: curl (always) + GDAL's ogr2ogr (for clip/convert of shapefile/OSM)
#    Ubuntu/WSL:  sudo apt-get install gdal-bin
#    macOS:       brew install gdal
export HONUA_ADMIN_API_KEY=...           # admin key for the target server
export HONUA_SERVER=http://localhost:8080

# list the curated catalog
python3 scripts/provisioner/provision_area.py --list

# end-to-end: load San Francisco County (GEOID 06075) roads, publish + tile
python3 scripts/provisioner/provision_area.py \
  --source census-tiger --product roads --area geoid:06075 --tile

# end-to-end: load OSM buildings for a downtown bbox (lon/lat envelope)
python3 scripts/provisioner/provision_area.py \
  --source osm-geofabrik --product buildings \
  --area bbox:-122.42,37.77,-122.40,37.79 --tile

# just fetch + clip and write the GeoJSON locally (no server needed)
python3 scripts/provisioner/provision_area.py \
  --source census-tiger --product county-boundaries --area geoid:06075 --fetch-only
```

### AREA forms

| Form | Example | Used by |
|------|---------|---------|
| `bbox:minLon,minLat,maxLon,maxLat` | `bbox:-122.5,37.7,-122.3,37.8` | OSM (Overpass), bbox-clip of any polygon source |
| `geoid:DD` | `geoid:06` (California, state FIPS) | TIGER state-scoped products |
| `geoid:DDDDD` | `geoid:06075` (San Francisco County) | TIGER county-scoped products + row-filter |
| `geoid:DDDDDDD` | `geoid:0667000` (place GEOID) | TIGER place products (future) |

## What the one end-to-end import actually does (real vs scripted vs TODO)

For **`census-tiger`** (fully wired) and **`osm-geofabrik`** (Overpass path
wired):

| Step | Status | Detail |
|------|--------|--------|
| 1. Resolve recipe + URL | **real** | Reads `catalog.json`, substitutes `{stateFips}`/`{countyGeoid}`/`{bboxSWNE}` into the `urlTemplate`. |
| 2. Fetch | **real** | TIGER: HTTPS GET of the per-county/state `.zip` from `www2.census.gov` (verified live). OSM: Overpass API GET by bbox. |
| 3. Clip / filter | **real (needs GDAL)** | TIGER county boundaries: `ogr2ogr -where "GEOID = '06075'"`. bbox sources: `ogr2ogr -clipsrc`. Output is GeoJSON. |
| 4. Import | **real** | Multipart `POST /api/v1/admin/import/upload` (the same path the console uses); large files auto-queue as tracked background jobs. |
| 5. Publish | **real** | `POST /api/v1/admin/connections/{id}/layers` with geometry type from the catalog. |
| 6. Tile | **real, opt-in** | `--tile` queues a `seed` tile-operation. If a batch backend is configured (`ITileCacheJobService.IsEnabled`), it runs on **GP-on-Batch (Fargate Spot, scale-to-zero)**; otherwise the in-process channel worker. |

**Scripted-not-automated / TODO** (surfaced as explicit messages, never faked):

- **OSM Overpass → GeoJSON conversion** relies on `ogr2ogr` reading the
  Overpass result. For larger areas, slice 2 should switch to the
  `geofabrik-extract` path (`osmium extract -b <bbox>` on a regional
  `.osm.pbf`, then `ogr2ogr`). The tool prints these exact commands if the
  conversion needs it.
- **`overture`, `usgs-nhd`, `fema-nfhl`** are curated **placeholders** -- the
  tool refuses with a pointer to `areaParam.notes`, which records the intended
  recipe (DuckDB bbox over Overture GeoParquet; HUC/county FileGDB for
  NHD/NFHL). No fake imports.
- **`addresses`** (TIGER) is a `geocoder` feedstock; the tool guards it (see
  below).

## Cost posture

- **No bulk data in the repo.** The catalog stores URL templates plus a single
  `sampleUrl` per product for docs/testing. Nothing large is committed.
- **Fetch is area-scoped.** TIGER is downloaded per county/state (the smallest
  published file), then row-filtered/clipped. OSM uses Overpass bbox queries,
  not planet dumps.
- **Tiling → S3 + Spot.** PMTiles land in S3 (object-store model); the tiling
  job runs on Fargate Spot via GP-on-Batch and scales to zero when idle.

## Extension pattern: adding a new source

Adding a source is a **data edit** to `catalog.json` (validated by
`schema.json`) -- no code change for the common cases.

1. Add a `source` object: `id`, `name`, `publisher`, `homepage`, `license`
   (`spdx` + `url`, and `attributionRequired`/`attributionText` for ODbL/CDLA),
   and an `extractMethod` (`bbox` | `admin-boundary` | `census-geoid`).
2. Declare `areaParam.accepts` (which AREA inputs it understands) and document
   the recipe intent in `areaParam.notes`.
3. Add one or more `products`. Each product needs `product`, `layerName`,
   `geometryType`, a `format` (one of Honua's import formats), and a `fetch`
   recipe:
   - **`http-template`** -- `urlTemplate` with `{stateFips}`/`{countyGeoid}`/
     `{placeGeoid}` placeholders; set `clip: true` if the file still needs
     clipping after download.
   - **`overpass`** -- `urlTemplate` with the `{bboxSWNE}` placeholder.
   - **`geofabrik-extract`** -- regional `.osm.pbf` + `osmium` clip (slice 2).
4. Set `status: "available"` once a working recipe exists; leave
   `status: "placeholder"` while it is curated-but-unimplemented (the tool will
   refuse cleanly).

If a source needs a fetch shape the four `fetch.kind`s don't cover (e.g. a
WFS/OGC endpoint), add a new `kind` and a small handler branch in
`fetch_and_prepare()` -- that is the only code touch-point.

## Future source-products: geocoding and routing on GP-Batch

The catalog's `product.sourceProduct` field already distinguishes what a product
is built into: `features` (this slice), `tiles`, `geocoder`, or `router`. The
key idea is that **geocoding and routing are just additional build jobs over the
same imported feature layers, executed on the same GP-on-Batch engine** that
already runs tiling.

```
                       imported feature layer (PostGIS)
                                   |
          +------------------------+------------------------+
          |                        |                        |
     features layer          PMTiles tiling           future products
     (served today)          (GP-Batch today)         (GP-Batch, next)
                                                    |              |
                                            geocoder build   router build
                                            (locator index)  (routing graph)
```

- **Geocoding** -- TIGER `addresses` (ADDRFEAT, address ranges) and OSM POI are
  the feedstock. A future `build-geocoder` GP-Batch job consumes the published
  address layer and emits a locator index (the same object-store + Spot model as
  tiling). The provisioner already tags `census-tiger/addresses` with
  `sourceProduct: "geocoder"` and **guards** it: it will import the features but
  refuse to claim a locator was built until the GP-Batch job exists.
- **Routing** -- a road-network layer (TIGER `roads` or OSM `roads`) is the
  feedstock for a future `build-router` GP-Batch job that contracts the network
  into a routing graph. Same dispatch path as tiling
  (`TileOperationsEndpoints` → `ITileCacheJobService` batch backend): a new
  job type (`build-geocoder` / `build-router`) submitted through the
  execution-job → Batch path, Fargate Spot, scale-to-zero.

Wiring these is deliberately **out of scope for slice 1** -- but the catalog
schema, the `sourceProduct` taxonomy, and the GP-Batch dispatch they ride are
all already in place, so each is an additive job type rather than a redesign.

## Files

- `data/open-data-catalog/catalog.json` -- the curated catalog.
- `data/open-data-catalog/schema.json` -- JSON Schema for the catalog.
- `data/open-data-catalog/README.md` -- catalog-dir orientation.
- `scripts/provisioner/provision_area.py` -- the area-import orchestrator.
