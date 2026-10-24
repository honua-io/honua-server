# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Independent fixture and identity assertions for the existing WFS query lane."""
from __future__ import annotations

import json
import os
import re
from pathlib import Path

import pytest
from shapely.geometry import box, shape

NAMES = ("alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "lambda")
CANONICAL_BBOX = (-122.495, 37.705, -122.455, 37.735)
WORKER_BBOX = (-122.411, 37.783, -122.408, 37.786)


def _coordinates_match(actual, expected):
    if isinstance(expected, (list, tuple)):
        return isinstance(actual, (list, tuple)) and len(actual) == len(expected) and all(
            _coordinates_match(left, right) for left, right in zip(actual, expected, strict=True))
    return actual == pytest.approx(expected, abs=1e-8, rel=0)


def _geometry_matches(actual, expected):
    """Retain type, structure and all ordinates within 1e-8, including Z if present.

    Normalize only representation: ring direction/start and component ordering.
    Coordinate counts, holes and geometry types must remain identical.
    """
    left = actual.normalize().__geo_interface__
    right = expected.normalize().__geo_interface__
    return left["type"] == right["type"] and _coordinates_match(left["coordinates"], right["coordinates"])


def authored_geometries():
    """Both maintained lane modes have authored data; neither uses observed outputs as truth."""
    if os.environ.get("HONUA_BASE_URL"):
        geometries = [{"type": "Point", "coordinates": [round(-122.49 + i * .015, 4), round(37.71 + i * .01, 4)]}
                      for i in range(9)]
        return dict(zip(NAMES, [*geometries, None], strict=True)), CANONICAL_BBOX, {"alpha", "beta", "gamma"}
    source = json.loads((Path(__file__).parents[2] / "fixtures/geometry-fixtures.json").read_text())["geometries"]
    keys = ("point", "multipoint", "linestring", "multilinestring", "polygonSimple", "polygonWithHole",
            "polygonWithMultipleHoles", "multipolygonSimple", "multipolygonWithHoles", "nullGeometry")
    return {name: source[key]["geojson"] for name, key in zip(NAMES, keys, strict=True)}, WORKER_BBOX, {"beta", "epsilon", "theta"}


def identity(feature, typename):
    """Retain source IDs through OGR's documented numeric FID/gml_id projections."""
    properties = feature.get("properties") or {}
    identifiers = [value for key, value in (("id", feature.get("id")),
                    ("objectid", properties.get("objectid")), ("gml_id", properties.get("gml_id"))) if value is not None]
    assert identifiers, "feature has no published identity"
    decoded = []
    prefixes = (typename, typename.rsplit(":", 1)[-1])
    for value in identifiers:
        if type(value) is int:
            decoded.append(value)
        elif isinstance(value, str) and re.fullmatch(r"[0-9]+", value):
            decoded.append(int(value))
        elif isinstance(value, str) and any(re.fullmatch(re.escape(prefix) + r"\.[0-9]+", value) for prefix in prefixes):
            suffix = value.rsplit(".", 1)[-1]
            assert re.fullmatch(r"[0-9]+", suffix), "invalid WFS identity suffix"
            decoded.append(int(suffix))
        else:
            raise AssertionError("invalid or unrelated WFS identity")
    assert len(set(decoded)) == 1, "conflicting published feature identities"
    return decoded[0]


def exact_rows(document, expected, typename, *, bounds=None):
    assert document.get("type") == "FeatureCollection", "expected a GeoJSON feature collection"
    features = document.get("features")
    assert isinstance(features, list) and len(features) == len(expected), "unexpected exact feature count"
    observed = {}
    for feature in features:
        identifier = identity(feature, typename)
        assert identifier not in observed, "duplicate feature identity"
        assert identifier in expected, "unexpected feature identity"
        observed[identifier] = feature["properties"]["name"]
        if bounds is not None:
            geometry = feature.get("geometry")
            assert geometry is not None, "spatial query returned null geometry"
            actual = shape(geometry)
            assert not actual.is_empty and actual.is_valid, "invalid spatial query geometry"
            assert actual.intersects(box(*bounds)), "spatial query returned out-of-bounds geometry"
            expected_geometry = shape(expected[identifier]["geometry"])
            assert actual.bounds == pytest.approx(expected_geometry.bounds, abs=1e-8, rel=0), "spatial query changed coordinate bounds"
            assert _geometry_matches(actual, expected_geometry), "spatial query changed geometry structure or coordinates"
    assert observed == {key: value["name"] for key, value in expected.items()}, "unexpected exact feature identity/name set"


def source_subset(document, typename):
    """Bind actual source IDs to independently authored names/geometries before selecting the window."""
    authored, bounds, expected_names = authored_geometries()
    features = document.get("features", [])
    assert document.get("type") == "FeatureCollection" and len(features) == len(authored), "source fixture is incomplete"
    assert {feature["properties"]["name"] for feature in features} == set(authored), "source fixture names differ"
    selected, seen = {}, set()
    for feature in features:
        identifier = identity(feature, typename)
        assert identifier not in seen, "source fixture has duplicate identities"
        seen.add(identifier)
        name = feature["properties"]["name"]
        geometry = feature.get("geometry")
        expected = authored[name]
        assert (geometry is None) == (expected is None), "source fixture geometry presence differs"
        if expected is not None:
            assert _geometry_matches(shape(geometry), shape(expected)), "source fixture geometry differs"
            if shape(expected).intersects(box(*bounds)):
                selected[identifier] = {"name": name, "geometry": expected}
    assert {row["name"] for row in selected.values()} == expected_names, "authored window changed"
    assert 0 < len(selected) < len(features) - 1, "bbox must select a nonempty strict subset with excluded geometry"
    return selected, bounds
