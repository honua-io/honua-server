# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Shared GDAL client calls for the GeoServices REST interop cases.

The seeded ``browser_compat`` layer 2000 holds exactly three points
(tests/seed/browser-compat.yaml), which is the oracle every case checks.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Callable

from .conftest import OgrResult, esrijson_dsn

SEEDED_NAMES = frozenset({"pt-alpha", "pt-beta", "pt-gamma"})


def read_features(ogr_run: Callable[..., OgrResult], layer_url: str, *open_options: str, **params: str) -> list[dict]:
    """Read a FeatureServer query through GDAL's ESRIJSON driver as GeoJSON features.

    ``open_options`` are ESRIJSON open options such as ``FEATURE_SERVER_PAGING=NO``.
    """
    options = [arg for option in open_options for arg in ("-oo", option)]
    result = ogr_run(["ogr2ogr", "-f", "GeoJSON", *options, "/vsistdout/", esrijson_dsn(layer_url, **params)])
    result.assert_success("ogr2ogr could not read the FeatureServer query through ESRIJSON")
    return json.loads(result.stdout)["features"]


def geoservices_error_code(dsn: str) -> int | None:
    """The ``error.code`` of the GeoServices JSON error the server returns for a DSN's URL.

    GDAL only reports that it could not open the layer; this reads the same response to
    confirm the server sent the structured ``{"error": {"code": ...}}`` body clients parse,
    not an HTML page or a dropped connection. Returns None when the body is not that shape.
    """
    request = urllib.request.Request(dsn.removeprefix("ESRIJSON:"), headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = response.read()
    except urllib.error.HTTPError as error:
        body = error.read()
    except OSError:
        return None
    try:
        code = json.loads(body).get("error", {}).get("code")
    except (ValueError, AttributeError):
        return None
    return code if isinstance(code, int) else None


def feature_names(features: list[dict]) -> list[str]:
    """The seeded ``name`` attribute of each feature, in response order."""
    return [feature["properties"]["name"] for feature in features]


# The seeded points' extent and a window of the same size well outside the service extent.
DATA_WINDOW = (-122.4260, 37.7790, -122.4140, 37.7700)
EMPTY_WINDOW = (-122.3200, 37.7290, -122.3080, 37.7200)
IMAGE_SIZE = (240, 180)


def ags_service_xml(mapserver_url: str, window: tuple[float, float, float, float], layer: str = "2000") -> str:
    """A GDAL WMS driver description of a MapServer export, in GDAL's AGS minidriver form.

    ``window`` is (upper-left x, upper-left y, lower-right x, lower-right y) in EPSG:4326.
    """
    ulx, uly, lrx, lry = window
    return (
        "<GDAL_WMS><Service name=\"AGS\">"
        f"<ServerUrl>{mapserver_url}</ServerUrl><BBoxOrder>xyXY</BBoxOrder>"
        f"<SRS>EPSG:4326</SRS><CRS>EPSG:4326</CRS><Layers>show:{layer}</Layers>"
        "<Transparent>TRUE</Transparent><ImageFormat>png</ImageFormat></Service>"
        f"<DataWindow><UpperLeftX>{ulx}</UpperLeftX><UpperLeftY>{uly}</UpperLeftY>"
        f"<LowerRightX>{lrx}</LowerRightX><LowerRightY>{lry}</LowerRightY>"
        f"<SizeX>{IMAGE_SIZE[0]}</SizeX><SizeY>{IMAGE_SIZE[1]}</SizeY></DataWindow>"
        "<BandsCount>4</BandsCount></GDAL_WMS>"
    )


def export_url(mapserver_url: str, window: tuple[float, float, float, float], layer: str = "2000") -> str:
    """The MapServer export request itself, for GDAL to read the server's own image over /vsicurl/."""
    ulx, uly, lrx, lry = window
    query = {"bbox": f"{ulx},{lry},{lrx},{uly}", "bboxSR": "4326", "imageSR": "4326",
             "size": f"{IMAGE_SIZE[0]},{IMAGE_SIZE[1]}", "format": "png", "transparent": "true",
             "layers": f"show:{layer}", "f": "image"}
    return f"{mapserver_url}/export?{urllib.parse.urlencode(query)}"


def export_image(ogr_run: Callable[..., OgrResult], service_xml: str, target: Path) -> dict:
    """Fetch the MapServer export through GDAL into a PNG and return gdalinfo statistics for it."""
    ogr_run(["gdal_translate", "-of", "PNG", service_xml, str(target)], timeout=60.0) \
        .assert_success("gdal_translate could not read the MapServer export through the AGS minidriver")
    info = ogr_run(["gdalinfo", "-json", "-stats", str(target)]).assert_success()
    return json.loads(info.stdout)


def band_spread(info: dict) -> float:
    """The largest per-band standard deviation: zero for a uniform image, positive once anything is drawn."""
    return max(float(band.get("stdDev") or 0.0) for band in info["bands"])
