# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Controlled WMTS assertion refusals; never emit deployment receipts."""
from __future__ import annotations

import io
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from xml.etree import ElementTree

import pytest
import requests
from PIL import Image, ImageDraw
from shared.cert_auth import AuthMode

from . import test_wmts
from .wmts_oracles import (
    POINT_RGB,
    POINTS,
    assert_fixture_pixels,
    assert_tile_denied,
    fixture_tile,
    world_pixel,
)


@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence():
    """Synthetic controls cannot enter any of the four live envelopes."""
    yield


def drawn(grid="WebMercatorQuad", zoom=5, *, shift=0, colour=(*POINT_RGB, 255)):
    row, column = fixture_tile(grid, zoom)
    image = Image.new("RGBA", (256, 256))
    draw = ImageDraw.Draw(image)
    for lon, lat in POINTS:
        x, y = world_pixel(grid, zoom, lon, lat)
        x, y = x - column * 256 + shift, y - row * 256 + shift
        if 0 <= x < 256 and 0 <= y < 256:
            draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=colour)
    return image, row, column


def test_authored_seed_binds_oracle_coordinates_and_style():
    root = Path(__file__).resolve().parents[3]
    seed = (root / "tests/seed/browser-compat.yaml").read_text()
    for lon, lat in POINTS:
        assert f"POINT({lon:.4f} {lat:.4f})" in seed
    assert '"circle-color":"#2D69A5"' in seed
    assert '"circle-radius":4' in seed
    protected = (root / "tests/seed/client-compat-auth-wave1.yaml").read_text()
    assert "SET maplibre_style = public_layer.maplibre_style" in protected
    assert "layer_id = 2010 AND public_layer.layer_id = 2000" in protected


def test_standard_grids_have_independent_known_anchor_values():
    assert world_pixel("WebMercatorQuad", 0, 0, 0) == (128, 128)
    assert world_pixel("WorldCRS84Quad", 0, 0, 0) == (256, 128)
    assert fixture_tile("WebMercatorQuad", 5) == (12, 5)
    assert fixture_tile("WorldCRS84Quad", 4) == (4, 5)


@pytest.mark.parametrize("grid,zoom", [("WebMercatorQuad", z) for z in (0, 2, 5, 10, 22)]
                         + [("WorldCRS84Quad", 4)])
def test_positive_style_and_location_reject_blank_pixels(grid, zoom):
    image, row, column = drawn(grid, zoom)
    assert assert_fixture_pixels(image, grid, zoom, row, column) > 0
    with pytest.raises(AssertionError, match="Missing authored"):
        assert_fixture_pixels(Image.new("RGBA", (256, 256)), grid, zoom, row, column)


@pytest.mark.parametrize("variant", ("shift", "wrong-style", "solid", "transparent", "dimensions"))
def test_status_and_dimensions_cannot_replace_expected_pixels(variant):
    image, row, column = drawn(shift=32 if variant == "shift" else 0,
                               colour=(255, 0, 0, 255) if variant == "wrong-style" else (*POINT_RGB, 255))
    if variant == "solid": image = Image.new("RGBA", (256, 256), (*POINT_RGB, 255))
    if variant == "transparent": image = Image.new("RGBA", (256, 256), (*POINT_RGB, 0))
    if variant == "dimensions": image = image.resize((128, 128))
    with pytest.raises(AssertionError):
        assert_fixture_pixels(image, "WebMercatorQuad", 5, row, column)


def test_wrong_grid_response_cannot_satisfy_second_grid():
    image, _, _ = drawn("WebMercatorQuad", 4)
    row, column = fixture_tile("WorldCRS84Quad", 4)
    with pytest.raises(AssertionError):
        assert_fixture_pixels(image, "WorldCRS84Quad", 4, row, column)


def denied(**changes):
    response = requests.Response()
    response.status_code = changes.get("status", 401)
    response.headers.update({"WWW-Authenticate": changes.get("challenge", "ApiKey realm=Honua"),
                             "Content-Type": changes.get("content_type", "application/xml")})
    response._content = changes.get("body", b'<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"><ows:Exception exceptionCode="AccessDenied"/></ows:ExceptionReport>')
    return response


def test_typed_tile_denial_positive():
    assert_tile_denied(denied(), "ApiKey")
    assert_tile_denied(denied(challenge="Bearer"), "Bearer")


@pytest.mark.parametrize("changes", [
    {"status": 200}, {"status": 403}, {"challenge": "Basic"}, {"challenge": ""},
    {"content_type": "image/png"}, {"body": b"\x89PNG\r\n\x1a\n"},
    {"body": b"\xff\xd8\xff"}, {"body": b"<ExceptionReport/>"},
    {"body": b'<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"/>'},
    {"body": b'<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"><ows:Exception exceptionCode="Other"/></ows:ExceptionReport>'},
])
def test_denial_requires_actual_challenge_and_no_image(changes):
    with pytest.raises((AssertionError, ElementTree.ParseError)):
        assert_tile_denied(denied(**changes), "ApiKey")


class Observations:
    def __init__(self): self.records = []
    def record(self, *args, **kwargs): self.records.append((args, kwargs))


def png(image):
    data = io.BytesIO()
    image.save(data, "PNG")
    return io.BytesIO(data.getvalue())


@pytest.mark.parametrize("blank", (False, True))
def test_actual_allowed_probe_records_only_real_expected_pixels(blank):
    image, row, column = drawn()
    if blank: image = Image.new("RGBA", (256, 256))
    client = SimpleNamespace(url="http://fixture/protected/WMTS", gettile=lambda **kwargs: png(image))
    record = Observations()
    with patch.object(test_wmts, "_protected_tile_request", return_value=(client, {"row": row, "column": column}, client.url)):
        if blank:
            with pytest.raises(AssertionError):
                test_wmts.test_auth02_credential_grants_access(client, AuthMode.API_KEY, "kvp", record)
            assert not record.records
        else:
            test_wmts.test_auth02_credential_grants_access(client, AuthMode.API_KEY, "kvp", record)
            assert record.records[0][0] == ("CERT-AUTH-02", "pass")
            assert record.records[0][1]["evidence_ref"] == client.url


@pytest.mark.parametrize("allowed_instead", (False, True))
def test_actual_denied_probe_cannot_use_success_or_admin_route(allowed_instead):
    def fetch(**kwargs):
        if allowed_instead: return png(Image.new("RGBA", (256, 256)))
        raise requests.HTTPError(response=denied())
    client = SimpleNamespace(url="http://fixture/protected/WMTS", gettile=fetch)
    record = Observations()
    with patch.object(test_wmts, "_protected_tile_request", return_value=(client, {}, client.url)):
        if allowed_instead:
            with pytest.raises(pytest.fail.Exception):
                test_wmts.test_auth01_anonymous_rejected(client, "anonymous", "kvp", record)
            assert not record.records
        else:
            test_wmts.test_auth01_anonymous_rejected(client, "anonymous", "kvp", record)
            assert record.records[0][0] == ("CERT-AUTH-01", "pass")


def test_protected_request_refuses_admin_substitution():
    prototype = SimpleNamespace(url="http://fixture/protected/WMTS",
        buildTileResource=lambda **kwargs: "http://fixture/api/v1/admin/services")
    with pytest.raises(AssertionError, match="escaped protected"):
        test_wmts._protected_tile_request(prototype, {}, "rest")
