"""Sample custom-code raster GP tool: GDAL/rasterio in-process raster processing.

This is the raster counterpart of ``buffer_tool.py``. It PROVES the in-image
raster-processing path: a GP tool processes a GeoTIFF *with GDAL* (here via the
``rasterio``/``osgeo`` bindings that link against the GDAL-full base image), not
via the Honua SDK. The SDK is data-transport only — a real tool would fetch the
input coverage to a local file (or a ``/vsi`` handle) with ``context.client`` and
upload/register the output GeoTIFF. Here we keep the data path trivial so the
sample is self-contained and offline.

What it does (a trivial band-math op — NDVI = (NIR - RED) / (NIR + RED)):

1. Resolve a 2-band (RED, NIR) input GeoTIFF. If ``params.input`` names a staged
   input artifact (see ``context.inputs``) use it; otherwise synthesize a tiny
   2-band GeoTIFF with ``osgeo.gdal`` so the sample runs with no inputs.
2. Read the bands with ``rasterio``, compute NDVI as float32 band math.
3. Write a single-band Float32 GeoTIFF (LZW-compressed) preserving the CRS +
   transform, and register it as the output artifact.

Entrypoint spec for this tool: ``raster_ndvi_tool:execute``.

Expected ``params_json`` (all optional)::

    {"input": "scene.tif", "out_name": "ndvi.tif", "size": 64}
"""

from __future__ import annotations

from honua_customcode_harness import GpContext, GpResult


def _synthesize_scene(path: str, size: int) -> None:
    """Write a tiny 2-band (RED, NIR) Float32 GeoTIFF using ``osgeo.gdal``.

    Demonstrates the *direct* GDAL C-binding path (``osgeo.gdal``), distinct from
    the higher-level ``rasterio`` read below — both link the same base libgdal.
    """
    import numpy as np
    from osgeo import gdal, osr

    gdal.UseExceptions()
    driver = gdal.GetDriverByName("GTiff")
    ds = driver.Create(path, size, size, 2, gdal.GDT_Float32)

    # A real-ish geotransform + CRS (UTM 33N) so reproject/warp would be valid.
    ds.SetGeoTransform((500000.0, 10.0, 0.0, 4600000.0, 0.0, -10.0))
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(32633)
    ds.SetProjection(srs.ExportToWkt())

    rng = np.random.default_rng(42)
    red = rng.uniform(0.05, 0.4, size=(size, size)).astype("float32")
    nir = rng.uniform(0.3, 0.9, size=(size, size)).astype("float32")
    ds.GetRasterBand(1).WriteArray(red)
    ds.GetRasterBand(2).WriteArray(nir)
    ds.FlushCache()
    # osgeo.gdal has no explicit close; dropping the final reference closes the dataset.
    del ds


def execute(context: GpContext) -> GpResult:
    params = context.params
    out_name = str(params.get("out_name", "ndvi.tif"))
    size = int(params.get("size", 64))

    context.log.info("raster NDVI tool starting (GDAL/rasterio in-process)")
    context.progress.report(5.0, "resolving input")

    try:
        import numpy as np
        import rasterio
    except ImportError as exc:  # pragma: no cover - baked into the image
        return GpResult.failed(f"raster stack not available in this runtime: {exc}")

    # Resolve the input: a staged input artifact, else synthesize one.
    input_key = params.get("input")
    if input_key and input_key in context.inputs:
        input_path = str(context.inputs[input_key])
        context.log.info(f"using staged input {input_key!r} -> {input_path}")
    else:
        input_path = str(context.workdir / "scene.tif")
        context.log.info("no input staged; synthesizing a 2-band scene with osgeo.gdal")
        _synthesize_scene(input_path, size)

    context.cancellation.raise_if_cancelled()
    context.progress.report(40.0, "reading bands")

    with rasterio.open(input_path) as src:
        if src.count < 2:
            return GpResult.failed(
                f"input must have >= 2 bands (RED, NIR); got {src.count}."
            )
        red = src.read(1).astype("float32")
        nir = src.read(2).astype("float32")
        profile = src.profile

    context.progress.report(70.0, "computing NDVI")
    denom = nir + red
    # Avoid divide-by-zero; NDVI is in [-1, 1].
    ndvi = np.where(denom == 0, 0.0, (nir - red) / denom).astype("float32")

    profile.update(count=1, dtype="float32", compress="lzw")
    out_path = context.workdir / out_name
    with rasterio.open(out_path, "w", **profile) as dst:
        dst.write(ndvi, 1)

    context.progress.report(90.0, "registering output")
    context.output.add_artifact(out_name, out_path)
    context.log.info(
        f"wrote {out_name} ({out_path.stat().st_size} bytes), "
        f"NDVI range [{float(ndvi.min()):.3f}, {float(ndvi.max()):.3f}]"
    )
    context.progress.report(100.0, "done")

    return GpResult.succeeded(f"NDVI GeoTIFF written to {out_name}")
