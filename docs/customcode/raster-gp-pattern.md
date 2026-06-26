# Raster/vector geoprocessing in custom-code GP tools

**TL;DR — GDAL is the engine, the Honua SDK is transport.** A custom-code GP tool
processes raster/vector data *with GDAL directly, in-process*, on local files. It uses
the Honua SDK only to fetch input coverages to a local file (or a `/vsi` handle) and to
upload/register the output GeoTIFF. The SDK is deliberately **not** a raster library —
there is no need for it to be one.

This pattern is identical across both custom-code runtimes:

| | Python runtime | .NET runtime |
|---|---|---|
| Image | [`docker/worker-customcode-python`](../../docker/worker-customcode-python/README.md) | [`docker/worker-customcode-dotnet`](../../docker/worker-customcode-dotnet/README.md) |
| GDAL interop | `osgeo.gdal` / `osgeo.ogr` / `osgeo.osr` + `rasterio` (GDAL-full base) | `OSGeo.GDAL` / `OSGeo.OGR` / `OSGeo.OSR` via `MaxRev.Gdal.Core` (+ `LinuxRuntime.Minimal`) |
| Init | implicit on import (`gdal.UseExceptions()` recommended) | `GdalBase.ConfigureAll()` once before use |
| Raster sample | `samples/raster_ndvi_tool.py` | `samples/RasterTool/RasterTool.cs` |
| Vector sample | `samples/buffer_tool.py` | `samples/BufferTool/BufferTool.cs` |

Both images are built on the **`ghcr.io/osgeo/gdal:ubuntu-full-3.12.4`** base, so the
full GDAL driver set is present. In-build sanity checks assert the bindings load and a
driver count `> 0`, so the images fail to build if raster/vector interop ever regresses.

## The three roles

1. **Honua SDK → input.** Fetch the input coverage to a local file (or open a GDAL
   `/vsi*` handle, e.g. `/vsicurl/…`, `/vsis3/…`). This is the *transport* step.
2. **GDAL → processing.** Open the local file / `/vsi` handle with GDAL (`rasterio` or
   `osgeo.gdal` in Python; `OSGeo.GDAL` in .NET), do the raster/vector work (reproject,
   warp, band math, raster algebra, vector ops), and write a local GeoTIFF. This is the
   *engine* step — GDAL does all raster I/O and geodesy.
3. **Honua SDK → output.** Register the output GeoTIFF as a job artifact (the harness
   uploads it to the job's S3 output prefix) and/or register it as a coverage via the
   SDK. The *transport* step again.

The harness already gives a tool a writable scratch `workdir`/`WorkDirectory`, a scoped
`client`/`Client` (the SDK), an `inputs`/`Inputs` map of staged input artifacts, and an
`output`/`Output` artifact sink. The tool's only job is steps 1→3.

## Python

```python
from honua_customcode_harness import GpContext, GpResult


def execute(context: GpContext) -> GpResult:
    import rasterio
    from rasterio.warp import calculate_default_transform, reproject, Resampling

    # 1. SDK is transport: resolve a staged input (or fetch via context.client).
    src_path = str(context.inputs["scene"])      # local file the harness staged

    # 2. GDAL is the engine: reproject to EPSG:4326 with rasterio (== GDAL warp).
    dst_path = context.workdir / "reprojected.tif"
    with rasterio.open(src_path) as src:
        transform, w, h = calculate_default_transform(
            src.crs, "EPSG:4326", src.width, src.height, *src.bounds)
        profile = src.profile
        profile.update(crs="EPSG:4326", transform=transform, width=w, height=h)
        with rasterio.open(dst_path, "w", **profile) as dst:
            for b in range(1, src.count + 1):
                reproject(
                    source=rasterio.band(src, b),
                    destination=rasterio.band(dst, b),
                    src_transform=src.transform, src_crs=src.crs,
                    dst_transform=transform, dst_crs="EPSG:4326",
                    resampling=Resampling.bilinear)

    # 3. SDK is transport: register the output GeoTIFF for upload.
    context.output.add_artifact("reprojected.tif", dst_path)
    return GpResult.succeeded("reprojected to EPSG:4326")
```

The low-level `osgeo.gdal`/`ogr`/`osr` bindings are equally available for direct
dataset/driver/CRS work (see `samples/raster_ndvi_tool.py`, which synthesizes a scene
with `osgeo.gdal` and computes NDVI band math with `rasterio`).

## .NET

```csharp
using Honua.CustomCode.Sdk;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;

public sealed class ReprojectTool : IGeoprocessingTool
{
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken ct)
    {
        GdalBase.ConfigureAll();                       // once, before using GDAL

        // 1. SDK is transport: the harness staged the input locally.
        var srcPath = context.Inputs["scene"];

        // 2. GDAL is the engine: warp to EPSG:4326 with the GDAL utilities API.
        var dstPath = Path.Combine(context.WorkDirectory, "reprojected.tif");
        using var src = Gdal.Open(srcPath, Access.GA_ReadOnly);
        var options = new GDALWarpAppOptions(["-t_srs", "EPSG:4326", "-r", "bilinear"]);
        using var dst = Gdal.Warp(dstPath, [src], options, null, null);
        dst.FlushCache();

        // 3. SDK is transport: register the output GeoTIFF for upload.
        context.Output.AddArtifact("reprojected.tif", dstPath);
        return Task.FromResult(GpResult.Succeeded("reprojected to EPSG:4326"));
    }
}
```

`OSGeo.OGR` (vector) and `OSGeo.OSR` (CRS) are available the same way. See
`samples/RasterTool/RasterTool.cs`, which creates a 2-band GeoTIFF with `OSGeo.GDAL` and
computes NDVI band math in-process.

## Why the SDK is not a raster library

GDAL already is the cross-language raster/vector engine (drivers, reprojection, warping,
algebra, format I/O) and is present in-process in both runtimes. Re-exposing any of that
through the Honua SDK would duplicate GDAL behind a thinner, lossy surface. The SDK's job
is the data path — authenticated fetch/upload/register of coverages — which complements,
and does not duplicate, the SDK's raster agents (coverage download/upload/metadata). Keep
the boundary crisp: **SDK moves bytes, GDAL processes pixels.**
