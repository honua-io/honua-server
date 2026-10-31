

- **A (reference):** GeoServices REST public `ScientificData/SeaTemperature` ImageServer on sampleserver6, a multidimensional `water_temp` cube with `StdTime` and `StdZ` dimensions.
- **B (Honua):** the genuine #3633/#3723 fixture. A CF NetCDF-4 file in LocalStack S3 goes through the production native scan (gdalmdiminfo) and derived Zarr, and is served as the `browser_compat` ImageServer from a trunk build.

The Honua values are checked against the independent oracle authored in `docker/client-compat/multidim-fixture/seed.py` (computed in `tools/ab_report.py` without reading server output). The SOAP shape is checked against what the reference returned to Pro.

## Result

| #3558 licensed acceptance | Status | Evidence |
|---|---|---|
| 1. Capture Pro 3.7's SOAP `GetMultidimensionalInfo` request; baseline A was nil | **met** | Pro's verbatim request, `runs/honua-trunk/requests.json` (sha256 `6905eea8…`). Nil baseline recorded on #3558, 2026-09-01, certified image `sha256:e6db39f6…` |
| 2. Adapt the builder result into the SOAP schema and capture response B | **met** | `runs/honua-trunk/GetMultidimensionalInfo.response.xml`. Pro received a non-nil `GetMultidimensionalInfoResponse` 78× (no faults). Property-set names equal the reference's, and every emitted key is one the reference uses (`ab-report.json` → `soap_shape`) |
| 3. Pro opens the layer and exposes/uses the real variable and dimension metadata | **not met** | Pro opens the layer and reads the variable (`sea_surface_temperature`, `degC`, "Sea Surface Temperature"). It cannot use the dimensions: it sees `latitude, longitude, time` with no values (`sea_surface_temperature(latitude=0, longitude=0, time=0)`), and `getDimensionValues` fails. The reference gives `water_temp(StdTime=69, StdZ=40)` with values. Root causes: #6491 and #6492 |
| 4. Captured-request regression in the ImageServer SOAP tests | **met** | `ImageServerMultidimensionalSoapTests.CapturedArcGisProRequest_ReturnsReferenceShapedPropertySets` replays Pro's exact bytes and asserts the reference-shaped key sets. `tools/replay.py` replays the whole capture without Pro (9/9 operations match) |

A/B on envelope, band values, nodata and metadata (`ab-report.json` → `metadata`, `band_values`):

| field | Honua via Pro | oracle | pass |
|---|---|---|---|
| variables | `sea_surface_temperature` | same | yes |
| unit / description | `degC` / Sea Surface Temperature | same | yes |
| dimension names | `latitude, longitude, time` | `StdTime` | no: #6492, #6491 |
| time values | none (call fails) | 2024-01-01, 2024-01-02 | no: #6491 |
| extent | -122.45, 37.73, -122.38, 37.80 | -122.525, 37.675, -122.325, 37.875 | no: #6493 |
| width × height | 64 × 64 | 4 × 4 | no: #6493 |
| pixel type | U8 | F32 | no: #6493 |
| nodata | 0 | -9999 | no: #6493 |
| band values | not readable from Pro (no slice is selectable) | 2 × 4 × 4, documented in `ab_report.py` | no: #6491, #6492 |

The same fixture's pixels do pass over REST: `runs/honua-trunk/native-scan-verify.log` shows all 32 Zarr values, both fill masks, and both time slices RGBA-exact against the oracle. The defects are in what the scan stores and what the service advertises, not in slicing.

## Defects found (filed)

- **#6491 (P1):** the scan's enrichment `gdalinfo -json /vsis3/…nc` always fails in a container. GDAL 3.13's netCDF driver needs Linux `userfaultfd` for `/vsi` files, and Docker's default seccomp blocks it. The job still succeeds, but with `temporal`, `extent` and `resolution` empty, so `time` never becomes `StdTime` and no coordinates are advertised. `gdalinfo` over the derived Zarr in the same container does return the geotransform, nodata and per-band time.
- **#6492 (P1):** `ImageServerMultidimensionalInfoBuilder` advertises horizontal axes (`latitude`/`longitude`) as slice dimensions. GeoServices REST server never does this.
- **#6493 (P2):** the service raster description (REST `?f=json`, SOAP `GetServiceInfo`) comes from layer 2000's base browser-compat raster (64×64 U8, constant 180), while slices come from the registered coverage.

## What Pro does (observations worth keeping)

- **Encoding:** the reference answers `GetMessageFormats` with `esriServiceCatalogMessageFormatSoapOrBin`, and Pro then uses GeoServices REST binary SOAP for every image operation. Honua advertises XML SOAP only, and Pro uses XML throughout. The reference's own XML binding faults `GetMultidimensionalInfo` (`soap:client`, "Error processing server request"). In `runs/reference` that is 1 XML fault next to 8 binary successes; a manual probe with the 10.8, 10.9, 10.91, 11.0, 11.1 and 11.3 namespaces faulted the same way. Reference GMI bodies are therefore binary and are compared structurally (ordered UTF-16 names, `tools/binary_soap_strings.py`).
- **Operations:** Pro sends to Honua `GetServiceInfo`, `GetVersion`, `IsFixedScaleImage`, `GetFields`, `GetKeyPropertiesX`, `ExecuteAISRequest(GetProperty ClientXADef)` and `GetMultidimensionalInfo`, all 200. Against the reference it also reaches `GetImage`/`ExportImage`, because its slices resolve.
- **Probes:** Pro's empty-body POST probes get 415 from Honua and 400 from the reference. Both are rejections, and neither changes Pro's flow.
- **Not finished on the reference:** the reference `RasterRead`/`SliceValues` steps did not finish within a bounded time (Pro walks `GetImage` across 2,760 global slices) and were stopped. They are not needed: values are compared against the oracle, not the reference.

## Redaction

Nothing here holds a token, licence file, credential or header:

- The proxy never writes HTTP headers.
- It records query parameter **names** only, never values.
- SOAP bodies are stored as redacted shapes (every scalar `[REDACTED]`) with SHA-256 fingerprints.
- A verbatim request is kept (`requests.json`) only when it passes a credential scan and the credential/URL scrub changes nothing.
- All traffic was anonymous: the fixture's admin key is used only by `verify.py` inside the compose network.
- Raw response bytes stayed outside the repository.
- `git grep -iE 'token=|password=|bearer|apikey=|X-API-Key'` over this directory matches only the scanner patterns in `tools/capture_proxy.py` and this sentence.

## Reproduce

```bash
# 1. Genuine fixture: build and seed the fixture, then run the native scan verifier.
export HONUA_CLIENT_COMPAT_ENVIRONMENT=Development HONUA_CLIENT_COMPAT_DEV_GRANT_EDITION=Pro \
       HONUA_CLIENT_COMPAT_STORAGE_PROVIDER=AwsS3 \
       HONUA_GITHUB_ACTOR_SECRET_FILE=<file> HONUA_GITHUB_TOKEN_SECRET_FILE=<file>
C="docker compose -p honua-3558 -f docker/client-compat/compose.yml \
   -f docs/internal/evidence/imageserver-multidim-soap-3558-20261022/tools/compose.capture.yml \
   --profile multidim-fixture"
$C up --build --wait honua gdal-worker seed      # wait for the seed container to exit 0
$C run --rm --no-deps multidim-fixture           # NetCDF -> native scan -> Zarr -> slices

# 2. Licensed captures (raw bytes outside the repository).
cd docs/internal/evidence/imageserver-multidim-soap-3558-20261022
export HONUA_3558_RAW=<dir outside the repo>
bash tools/run_capture.sh honua-trunk http://127.0.0.1:18658 18659 /services/browser_compat/ImageServer \
     sea_surface_temperature --rewrite-origin http://honua:5000=http://127.0.0.1:18659
bash tools/run_capture.sh reference https://sampleserver6.arcgisonline.com/arcgis 18661 \
     /services/ScientificData/SeaTemperature/ImageServer water_temp

# 3. A/B report, then the replay without Pro (needs only the fixture).
python -I tools/ab_report.py --raw-dir "$HONUA_3558_RAW"
python -I tools/replay.py runs/honua-trunk --origin http://127.0.0.1:18658 \
       --rewrite-origin http://honua:5000=http://127.0.0.1:18659
```

Traps:

- Never restart `honua` on a seeded fixture. The client-compat seed creates raster tables outside the migration journal, so a recreated server refuses the database (`SchemaExistsWithoutJournal`, or the contract-migration gate). Rebuild with `down -v` instead.
- `PUBLIC_BASE_URL` must stay `http://honua:5000` for the seed publishers; `--rewrite-origin` keeps Pro on the proxy.
- The PDAL stage of `docker/worker-gdal` builds from source and takes about 25 minutes cold on this host.

## Bindings

| item | value |
|---|---|
| server source | trunk `035ce3d243` (contains #5775 `714d5506`) |
| server image (local JIT build) | `sha256:238c1196873569d8bfc83502dd822ab04a368160c43a490d48da338a84cc475f` |
| GDAL worker image (local build, GDAL 3.13.1) | `sha256:316f82ee8fe3e1645f04f63a7039c6d201eb5df9bc70378afa88eddf85d59b9a` |
| LocalStack / PostGIS | `localstack/localstack:3.6.0`, `postgis/postgis:16-3.4` |
| reference | `https://sampleserver6.arcgisonline.com/arcgis/services/ScientificData/SeaTemperature/ImageServer` (GeoServices REST server 10.91) |
| capture date | 2026-10-22 |

`manifest.json` lists the SHA-256 of every committed file in this bundle.
