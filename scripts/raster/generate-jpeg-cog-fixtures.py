"""Generate JPEG-compressed COG fixtures with real GDAL for honua-server #4205.

Run inside the GDAL image (the osgeo.gdal bindings are not needed on the host):

  docker run --rm -v "$PWD:/w" -w /w ghcr.io/osgeo/gdal:ubuntu-full-3.13.1 \
    python3 scripts/raster/generate-jpeg-cog-fixtures.py tests/dotnet/Honua.Core.Tests/Raster/CogParser/Fixtures

Emits, for each fixture:
  <name>.tif   - the GDAL-written GeoTIFF, JPEG tiles with GDAL's default JPEGTABLESMODE=1
                 (quantization tables live only in the shared JPEGTables tag 347)
  <name>.bin   - GDAL's own decode of that file (libtiff + libjpeg), in TIFF tile order,
                 samples interleaved per pixel: the independent expected pixels

JPEG is lossy, so the reference is what GDAL decodes from the file rather than the
synthetic input; a correctly assembled tile must decode to (nearly) the same samples.
"""
import hashlib
import json
import os
import sys

import numpy as np
from osgeo import gdal, osr

gdal.UseExceptions()

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)

# Same Web-Mercator grid as generate-cog-fixtures.py: a 128 px tile at z=8 is tile 8/0/0.
GEOTRANSFORM = (-20037508.342789244, 1222.992452562495, 0.0, 20037508.342789244, 0.0, -1222.992452562495)
SIZE = 128


def make_data(bands):
    """Smooth, photograph-like bands so JPEG round-trips stay close to the input."""
    yy, xx = np.mgrid[0:SIZE, 0:SIZE]
    planes = [
        (xx * 2 + 8) % 256,
        (yy * 2 + 16) % 256,
        ((xx + yy) + 64) % 256,
    ]
    return np.stack(planes[:bands]).astype(np.uint8)


def source_dataset(bands):
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(3857)
    mem = gdal.GetDriverByName("MEM").Create("", SIZE, SIZE, bands, gdal.GDT_Byte)
    mem.SetGeoTransform(GEOTRANSFORM)
    mem.SetProjection(srs.ExportToWkt())
    data = make_data(bands)
    for band in range(bands):
        mem.GetRasterBand(band + 1).WriteArray(data[band])
    if bands == 3:
        for band, interp in enumerate((gdal.GCI_RedBand, gdal.GCI_GreenBand, gdal.GCI_BlueBand)):
            mem.GetRasterBand(band + 1).SetColorInterpretation(interp)
    return mem


def write(name, bands, driver, options):
    path = os.path.join(OUT, name + ".tif")
    gdal.Translate(path, source_dataset(bands), format=driver, creationOptions=options)

    ds = gdal.Open(path)
    structure = ds.GetMetadata("IMAGE_STRUCTURE")
    assert structure.get("COMPRESSION") in ("JPEG", "YCbCr JPEG"), (name, structure)
    back = ds.ReadAsArray()
    if back.ndim == 2:
        back = back[np.newaxis, :, :]
    ds = None

    blob = np.transpose(back, (1, 2, 0)).tobytes()
    with open(os.path.join(OUT, name + ".bin"), "wb") as f:
        f.write(blob)

    return {
        "name": name,
        "driver": driver,
        "options": options,
        "bands": bands,
        "image_structure": structure,
        "tif_bytes": os.path.getsize(path),
        "sha256_expected": hashlib.sha256(blob).hexdigest(),
    }


manifest = [
    # The documented cloud-COG imagery recipe: the COG driver defaults three-band JPEG to
    # YCbCr with 2x2 chroma subsampling and quantization-only shared JPEGTables.
    write("jpeg_ycbcr_rgb_uint8", 3, "COG",
          ["COMPRESS=JPEG", "BLOCKSIZE=128", "OVERVIEWS=NONE", "QUALITY=90"]),
    # Single-band imagery (panchromatic, hillshade).
    write("jpeg_gray_uint8", 1, "COG",
          ["COMPRESS=JPEG", "BLOCKSIZE=128", "OVERVIEWS=NONE", "QUALITY=90"]),
    # PHOTOMETRIC=RGB keeps the stream in RGB, so the served tile must say so explicitly.
    write("jpeg_rgb_uint8", 3, "GTiff",
          ["TILED=YES", "BLOCKXSIZE=128", "BLOCKYSIZE=128", "COMPRESS=JPEG",
           "PHOTOMETRIC=RGB", "JPEG_QUALITY=90"]),
]

print(json.dumps({"gdal": gdal.__version__, "fixtures": manifest}, indent=2))
