# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Author the committed imagery.classify scene. Input only - never an expected output.

The scene is a 4x4 three-band Byte GeoTIFF in EPSG:4326 whose origin is
(10.0, 20.0) with 0.5-degree cells, so it covers x[10, 12] y[18, 20].

Each pixel is one of three committed spectral signatures displaced by a small
deterministic offset, so classification is a real nearest-centroid computation
rather than a lookup of the signature itself. The intended layout, row-major
from the north-west corner, is:

    water water vegetation vegetation
    water water vegetation vegetation
    soil  soil  soil       vegetation
    soil  soil  water      water

Offsets for pixel i are (((i%3)-1)*5, ((i%5)-2)*4, ((i%4)-1)*3) on red/green/blue.
Every displaced pixel stays well inside its centroid's decision region; the
generator asserts it, so a fixture edit that made a pixel ambiguous fails here
instead of silently weakening the proof.

Rebuild with the pinned production GDAL image, this directory mounted at /fixtures:
    docker run --rm -v "$PWD":/fixtures -w /fixtures --entrypoint python3 \
        <GDAL_BASE_IMAGE> generate-scene.py
"""
import numpy as np
from osgeo import gdal, osr

CLASSES = {1: (20, 40, 80), 2: (40, 90, 30), 3: (120, 90, 60)}
LAYOUT = [
    1, 1, 2, 2,
    1, 1, 2, 2,
    3, 3, 3, 2,
    3, 3, 1, 1,
]


def main() -> int:
    gdal.UseExceptions()
    bands = [[], [], []]
    for index, class_id in enumerate(LAYOUT):
        centroid = CLASSES[class_id]
        offsets = (((index % 3) - 1) * 5, ((index % 5) - 2) * 4, ((index % 4) - 1) * 3)
        pixel = tuple(max(0, min(255, centroid[b] + offsets[b])) for b in range(3))

        ranked = sorted(
            (sum((pixel[b] - value[b]) ** 2 for b in range(3)) ** 0.5, key)
            for key, value in CLASSES.items()
        )
        assert ranked[0][1] == class_id, (index, pixel, ranked)
        assert ranked[1][0] - ranked[0][0] > 15, ("ambiguous pixel", index, pixel, ranked)

        for b in range(3):
            bands[b].append(pixel[b])

    dataset = gdal.GetDriverByName("GTiff").Create("classify-scene.tif", 4, 4, 3, gdal.GDT_Byte)
    dataset.SetGeoTransform((10.0, 0.5, 0.0, 20.0, 0.0, -0.5))
    reference = osr.SpatialReference()
    reference.ImportFromEPSG(4326)
    dataset.SetProjection(reference.ExportToWkt())
    for index, values in enumerate(bands, start=1):
        band = dataset.GetRasterBand(index)
        band.WriteArray(np.array(values, dtype=np.uint8).reshape(4, 4))
    dataset.FlushCache()

    for index, values in enumerate(bands, start=1):
        print(f"band {index}: {values}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
