# honua-worker-customcode-python (Phase 1)

The **Python custom-code GP runtime**. This image is the sandboxed unit that
runs inside a per-job AWS Batch container: it clones a user's GP tool at a
**pinned git SHA**, runs it under a **scoped Honua SDK client**, and uploads the
tool's output artifacts to the job's S3 output prefix.

It is **separate** from `docker/worker-gdal` — that image is the .NET native ETL
worker and ships no Python/git/pip. This image is the opposite: a Python runtime
on a GDAL-full base with the geospatial stack + `honua-sdk` baked in.

## Image contents

- **Base:** `ghcr.io/osgeo/gdal:ubuntu-full-3.12.4` (pinned) — provides the GDAL
  python bindings (`osgeo.gdal/ogr/osr`) and the full driver set.
- Added: `git`, `python3-pip`, `ca-certificates`, build toolchain.
- Pre-installed (no per-job restore for the common case): `rasterio`,
  `geopandas`, `shapely`, `pyproj`, `fiona`, `boto3`, and **`honua-sdk==0.1.4`**.

### First-class GDAL interop

A raster/vector GP tool processes data **with GDAL directly, in-process** — the
SDK is data-transport only, not a raster library. This image gives user code the
full GDAL Python interop surface:

- `osgeo.gdal` / `osgeo.ogr` / `osgeo.osr` — the GDAL/OGR/OSR Python C-bindings
  (from the GDAL-full base), for direct dataset/driver/CRS work.
- `rasterio` (+ `fiona`, `geopandas`, `pyproj`, `shapely`) — the higher-level geo
  stack, linking against the **same** base `libgdal`.

The in-build sanity check imports the full binding set and asserts
`gdal.GetDriverCount() > 0` / `ogr.GetDriverCount() > 0`, so the image fails to
build if the raster/vector interop ever regresses. See the raster sample below
and [`docs/customcode/raster-gp-pattern.md`](../../docs/customcode/raster-gp-pattern.md)
for the SDK-as-transport / GDAL-as-engine pattern.
- The harness package `honua_customcode_harness` is installed; the
  `honua-customcode-harness` console script is the `ENTRYPOINT`.
- Runs as non-root `uid 1001` (matching `worker-gdal`). Scratch tree `/work`.

> Docker build is **CI/local-Docker verified** — it is not built in the agent
> environment (no daemon). The Dockerfile is correct-by-construction: a pinned,
> verified base tag and an in-build import/PATH check
> (`python3 -c "import honua_sdk, rasterio, ... ; import osgeo.gdal"` +
> `command -v honua-customcode-harness`) fails the build early if a wheel does
> not link or the entrypoint is missing.

## The contract (job inputs)

The Batch container receives the auth spine as **env vars** (standard Batch
secret injection, so the token is strippable):

- `HONUA_BASE_URL`
- `HONUA_JOB_TOKEN` — the scoped, job-bound bearer token (from the server)

The `customcode.*` parameters are resolved in this order (see
`harness/honua_customcode_harness/jobspec.py`):

1. **A mounted job-spec file** if `CUSTOMCODE_JOB_SPEC` points at a readable JSON
   file (preferred for large `params_json`/`deps`). The auth spine is *never*
   read from this file — only from env.
2. Otherwise **discrete `CUSTOMCODE_*` env vars** (cleanest for small jobs;
   used by local/dev runs).

| Spec key | Env var | Notes |
|---|---|---|
| `runtime` | `CUSTOMCODE_RUNTIME` | e.g. `python` |
| `repo_url` | `CUSTOMCODE_REPO_URL` | https/ssh git URL |
| `git_ref` | `CUSTOMCODE_GIT_REF` | **40-hex SHA**, validated |
| `entrypoint` | `CUSTOMCODE_ENTRYPOINT` | `module:func` |
| `deps_manifest` | `CUSTOMCODE_DEPS_MANIFEST` | requirements file, repo-relative |
| `params_json` | `CUSTOMCODE_PARAMS_JSON` | inline JSON or `@/path` |
| `output_prefix` | `CUSTOMCODE_OUTPUT_PREFIX` | `s3://bucket/prefix` |
| `declared_scope` | `CUSTOMCODE_DECLARED_SCOPE` | advisory scope list |
| `output_max_bytes` | `CUSTOMCODE_OUTPUT_MAX_BYTES` | default 1 GiB |

## Harness flow

1. **Load + validate inputs.** `git_ref` must be a 40-hex SHA; `output_prefix`
   must be `s3://…`; `entrypoint` must be `module:func`.
2. **Clone pinned source.** `git init` + `fetch --depth 1 <sha>` +
   `checkout --detach <sha>`, then **assert `git rev-parse HEAD == <sha>`** —
   fail hard on mismatch.
3. **Restore extra deps** (`pip install -r <deps_manifest>`) — the SDK + geo
   stack are already baked in, so this only pulls user extras.
4. **Build the scoped client, then strip credentials.** Construct
   `HonuaClient(base_url, auth_provider=StaticAuthProvider({"Authorization":
   f"Bearer {HONUA_JOB_TOKEN}"}))`, **then delete** `HONUA_JOB_TOKEN`,
   `AWS_CONTAINER_CREDENTIALS_*`, `ECS_CONTAINER_METADATA_URI*`, and any static
   AWS keys from `os.environ` **before importing user code** — so the tool can
   neither read the raw token nor assume the Batch task role. An invariant check
   (`assert_credentials_stripped`) runs after the scrub.
5. **Import + run.** Load the `module:func` entrypoint from the source root,
   build a `GpContext`, call `func(context)`.
6. **Upload + report.** Upload registered artifacts to `output_prefix`, enforce
   the output-size cap, and map the returned `GpResult` to an exit code:
   `0` success, `1` tool failure, `2` harness/setup error, `3` cancelled.

## The `execute(context)` contract

A tool ships a module exposing `def execute(context) -> GpResult`:

```python
from honua_customcode_harness import GpContext, GpResult

def execute(context: GpContext) -> GpResult:
    distance = context.params["distance"]          # parsed params_json
    context.log.info("starting")                   # job logs
    context.progress.report(50.0, "buffering")     # coarse progress
    context.cancellation.raise_if_cancelled()      # cooperative cancel
    out = context.workdir / "out.geojson"          # writable scratch
    out.write_text("...")
    context.output.add_artifact("out.geojson", out)  # staged for S3 upload
    # context.client -> pre-authed scoped HonuaClient (raw token never exposed)
    # context.inputs -> resolved input artifacts (name -> local path)
    return GpResult.succeeded("done")              # or GpResult.failed("why")
```

`context` exposes: `.params`, `.inputs`, `.client` (scoped `HonuaClient`),
`.output.add_artifact(name, path)`, `.progress.report(pct, phase)`,
`.log.info/warn(...)`, `.cancellation`, `.workdir`.

Two samples live under [`harness/samples/`](harness/samples/):

- [`buffer_tool.py`](harness/samples/buffer_tool.py) (entrypoint
  `buffer_tool:execute`): buffers a WKT geometry and writes GeoJSON (vector).
- [`raster_ndvi_tool.py`](harness/samples/raster_ndvi_tool.py) (entrypoint
  `raster_ndvi_tool:execute`): the **raster** sample — synthesizes a 2-band
  GeoTIFF with `osgeo.gdal`, reads it with `rasterio`, computes NDVI band math,
  and writes an LZW-compressed Float32 GeoTIFF. This proves the in-image
  raster-processing path (GDAL is the engine, the SDK is transport).

## Tests (offline)

`harness/tests/` unit-tests the git-ref validation (rejects non-SHA), the
IMDS/token strip (vars gone after client build, observed by the tool), context
construction + artifact sink + size cap, `GpResult` shape, entrypoint loading,
and the full flow with a fake SDK/git/pip/uploader. Run:

```bash
cd docker/worker-customcode-python/harness
with-build-lock python3 -m pytest
```
