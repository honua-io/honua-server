#!/usr/bin/env python3
"""Independent value oracle for the GP output-store deployment fixture.

The input is 500 WGS84 points on a known integer grid. This verifies decoded
values, not GDAL's formatting or a snapshot of a previous output. Raster nodata
does not apply to this vector fixture; every point must have two finite ordinates.
"""

import json
import math
import sys
from pathlib import Path


def verify(document):
    if document.get("type") != "FeatureCollection":
        raise ValueError("expected a GeoJSON FeatureCollection")
    features = document.get("features", [])
    if len(features) != 500:
        raise ValueError("expected exactly 500 features")
    seen = set()
    for feature in features:
        identifier = feature.get("properties", {}).get("id")
        if type(identifier) is not int or not 0 <= identifier < 500 or identifier in seen:
            raise ValueError("missing, duplicated or incorrect feature id")
        seen.add(identifier)
        geometry = feature.get("geometry") or {}
        if feature.get("type") != "Feature" or geometry.get("type") != "Point":
            raise ValueError(f"feature {identifier}: expected a Point feature")
        expected = ((-1578583 + identifier % 100) / 10000,
                    (213069 + identifier % 50) / 10000)
        coordinates = geometry.get("coordinates", [])
        if len(coordinates) != 2 or any(
            type(actual) not in (int, float)
            or not math.isfinite(actual)
            or not math.isclose(actual, value, rel_tol=0, abs_tol=1e-10)
            for actual, value in zip(coordinates, expected)
        ):
            raise ValueError(f"feature {identifier}: incorrect WGS84 ordinates")
    crs = document.get("crs")
    if crs is not None and (
        crs.get("type") != "name"
        or crs.get("properties", {}).get("name") not in (
            "urn:ogc:def:crs:OGC:1.3:CRS84", "urn:ogc:def:crs:EPSG::4326"
        )
    ):
        raise ValueError("unexpected coordinate reference system")


if __name__ == "__main__":
    verify(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
