"""Generate reference COG fixtures with real GDAL (via rasterio) for honua-server #2854.

Emits, for each fixture:
  <name>.tif   - the GDAL-produced GeoTIFF
  <name>.bin   - GDAL's own decoded pixel bytes, in TIFF tile layout, for cross-check

Tile layout for PLANARCONFIG_CONTIG is row-major with samples interleaved per pixel,
which is exactly numpy's (rows, cols, bands) C-order tobytes().
"""
import numpy as np
import rasterio
from rasterio.transform import from_origin
from rasterio.crs import CRS
import os, sys, json, hashlib

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)

# Web-Mercator origin/scale so CogMetadataExtractor sees a real EPSG:3857 georeference.
TRANSFORM = from_origin(-20037508.342789244, 20037508.342789244, 1222.992452562495, 1222.992452562495)
CRS3857 = CRS.from_epsg(3857)


def make_data(h, w, bands, dtype):
    """Smooth ramp + deterministic pseudo-noise.

    The ramp makes the horizontal predictor genuinely effective; the noise carries
    enough entropy that LZW fills past 511/1023/2047 codes and hits table-full +
    ClearCode, exercising every code-width transition rather than only 9-bit codes.
    """
    rng = np.random.default_rng(20250715)
    yy, xx = np.mgrid[0:h, 0:w]
    info = np.iinfo(dtype)
    span = min(info.max, 4000)
    ramp = ((xx * 3 + yy * 2) % span).astype(np.int64)
    noise = rng.integers(0, max(2, span // 16), size=(h, w), dtype=np.int64)
    base = np.clip(ramp + noise, info.min, info.max)
    if bands == 1:
        return base.astype(dtype)[np.newaxis, :, :]
    out = np.empty((bands, h, w), dtype=dtype)
    for b in range(bands):
        out[b] = np.clip(base + b * (span // 8), info.min, info.max).astype(dtype)
    return out


def write(name, h, w, block, bands, dtype, compress, predictor):
    path = os.path.join(OUT, name + ".tif")
    data = make_data(h, w, bands, dtype)
    with rasterio.open(
        path, "w", driver="GTiff", height=h, width=w, count=bands, dtype=dtype,
        crs=CRS3857, transform=TRANSFORM,
        tiled=True, blockxsize=block, blockysize=block,
        compress=compress, predictor=predictor, interleave="pixel",
    ) as dst:
        dst.write(data)

    # Read back through GDAL: this is GDAL's decode of GDAL's own file, i.e. the
    # reference pixels our decoder must reproduce byte-for-byte.
    with rasterio.open(path) as src:
        actual = src.profile.get("compress", "none")
        actual = actual.name if hasattr(actual, "name") else str(actual)
        assert actual.lower() == compress.lower(), (name, actual, dict(src.profile))
        back = src.read()
        tags = src.tags(ns="IMAGE_STRUCTURE")

    if not np.array_equal(back, data):
        raise SystemExit(f"{name}: GDAL round-trip mismatch (fixture is not lossless)")

    # Emit per-tile expected bytes in TIFF tile order.
    tiles = []
    ty = (h + block - 1) // block
    tx = (w + block - 1) // block
    for row in range(ty):
        for col in range(tx):
            win = back[:, row * block:(row + 1) * block, col * block:(col + 1) * block]
            # Tiles are padded to full block size in the file; pad the reference the same way.
            pad = np.zeros((bands, block, block), dtype=dtype)
            pad[:, : win.shape[1], : win.shape[2]] = win
            tiles.append(np.transpose(pad, (1, 2, 0)).tobytes())

    blob = b"".join(tiles)
    with open(os.path.join(OUT, name + ".bin"), "wb") as f:
        f.write(blob)

    return {
        "name": name, "width": w, "height": h, "block": block, "bands": bands,
        "dtype": dtype, "compress": compress, "predictor": predictor,
        "tiles": len(tiles), "tile_bytes": len(tiles[0]),
        "image_structure": tags,
        "tif_bytes": os.path.getsize(path),
        "sha256_expected": hashlib.sha256(blob).hexdigest(),
    }


manifest = [
    # LZW, predictor 1 vs 2, two bit depths.
    write("lzw_pred1_uint8", 128, 128, 128, 1, "uint8", "lzw", 1),
    write("lzw_pred2_uint8", 128, 128, 128, 1, "uint8", "lzw", 2),
    write("lzw_pred1_uint16", 128, 128, 128, 1, "uint16", "lzw", 1),
    write("lzw_pred2_uint16", 128, 128, 128, 1, "uint16", "lzw", 2),
    # Multi-sample predictor: stride is samplesPerPixel, a distinct failure mode.
    write("lzw_pred2_rgb_uint8", 128, 128, 128, 3, "uint8", "lzw", 2),
    # Multi-tile: exercises per-tile predictor reset and tile offset resolution.
    write("lzw_pred2_uint8_multitile", 256, 256, 128, 1, "uint8", "lzw", 2),
    # ZSTD, predictor 1 vs 2, two bit depths.
    write("zstd_pred1_uint8", 128, 128, 128, 1, "uint8", "zstd", 1),
    write("zstd_pred2_uint16", 128, 128, 128, 1, "uint16", "zstd", 2),
    # Existing codecs, to prove the fixture harness agrees with the paths already shipping.
    write("deflate_pred1_uint8", 128, 128, 128, 1, "uint8", "deflate", 1),
    write("none_uint8", 128, 128, 128, 1, "uint8", "none", 1),
]

print(json.dumps({"gdal": rasterio.__gdal_version__, "rasterio": rasterio.__version__,
                  "fixtures": manifest}, indent=2))
