# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Independent authored-fixture oracles for canonical WMTS tile observations.

Coordinates/styles belong to browser-compat.yaml, not a second server response.
Grid locations use the standard slippy-map/WorldCRS84Quad definitions, rather
than trusting the capabilities metadata that the server under test supplies.
"""
from __future__ import annotations

import math
from xml.etree import ElementTree

from PIL import Image

POINTS = ((-122.4194, 37.7749), (-122.4180, 37.7760), (-122.4210, 37.7735))
POINT_RGB = (45, 105, 165)
TILE_SIZE = 256


def world_pixel(grid: str, zoom: int, lon: float, lat: float) -> tuple[float, float]:
    assert isinstance(zoom, int) and 0 <= zoom <= 22, "Unsupported fixture zoom"
    scale = TILE_SIZE * 2**zoom
    if grid == "WebMercatorQuad":
        latitude = math.radians(lat)
        return ((lon + 180) / 360 * scale,
                (1 - math.asinh(math.tan(latitude)) / math.pi) / 2 * scale)
    assert grid == "WorldCRS84Quad", f"Unsupported fixture grid {grid}"
    return (lon + 180) / 180 * scale, (90 - lat) / 180 * scale


def fixture_tile(grid: str, zoom: int) -> tuple[int, int]:
    x, y = world_pixel(grid, zoom, *POINTS[0])
    return math.floor(y / TILE_SIZE), math.floor(x / TILE_SIZE)


def assert_fixture_pixels(image: Image.Image, grid: str, zoom: int,
                          row: int, column: int) -> int:
    """Require the authored blue point style at independently expected positions.

    Small anti-aliasing/rounding windows are bounded by the authored radius4;
    painted pixels elsewhere cannot substitute for expected feature locations.
    """
    assert image.size == (TILE_SIZE, TILE_SIZE), f"Wrong tile dimensions {image.size}"
    expected = []
    for lon, lat in POINTS:
        x, y = world_pixel(grid, zoom, lon, lat)
        x, y = x - column * TILE_SIZE, y - row * TILE_SIZE
        if 0 <= x < TILE_SIZE and 0 <= y < TILE_SIZE:
            expected.append((x, y))
    assert expected, "Positive tile contains no authored fixture point"
    pixels = image.convert("RGBA")
    painted = [(x, y) for y in range(TILE_SIZE) for x in range(TILE_SIZE)
               if pixels.getpixel((x, y))[3] > 0]
    for px, py in expected:
        matches = sum(1 for x, y in painted
                      if math.hypot(x + 0.5 - px, y + 0.5 - py) <= 3
                      and pixels.getpixel((x, y))[3] >= 200
                      and all(abs(actual - target) <= 5 for actual, target in
                              zip(pixels.getpixel((x, y))[:3], POINT_RGB, strict=True)))
        assert matches >= 3, f"Missing authored point/style pixels at {(px, py)} in {grid}/{zoom}"
    assert all(any(math.hypot(x + 0.5 - px, y + 0.5 - py) <= 6.5 for px, py in expected)
               for x, y in painted), "Painted pixels outside authored feature locations"
    return len(painted)


def assert_tile_denied(response, expected_scheme: str) -> None:
    """Observe actual tile authorization; an admin denial or image is no proof."""
    assert response.status_code == 401, f"Protected tile returned {response.status_code}"
    assert expected_scheme.lower() in response.headers.get("WWW-Authenticate", "").lower()
    assert "xml" in response.headers.get("Content-Type", "").lower()
    assert b"\x89PNG" not in response.content and b"\xff\xd8\xff" not in response.content
    root = ElementTree.fromstring(response.content)
    assert root.tag == "{http://www.opengis.net/ows/1.1}ExceptionReport", root.tag
    exceptions = root.findall("{http://www.opengis.net/ows/1.1}Exception")
    codes = [exception.get("exceptionCode") for exception in exceptions]
    assert codes and all(code == "AccessDenied" for code in codes), f"WMTS auth exception codes: {codes}"
