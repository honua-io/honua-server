# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Geometry generators for comprehensive spatial testing.

Supports all GeoJSON geometry types:
- Point, MultiPoint
- LineString, MultiLineString
- Polygon (with holes), MultiPolygon (with holes)
- GeometryCollection
- Null geometries
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from shapely.geometry import (
    LineString,
    MultiLineString,
    MultiPoint,
    Point,
    mapping,
    shape,
)


def _load_geometry_fixtures() -> dict[str, Any]:
    fixture_path = Path(__file__).resolve().parents[2] / "fixtures" / "geometry-fixtures.json"
    return json.loads(fixture_path.read_text(encoding="utf-8"))


_GEOMETRY_FIXTURES = _load_geometry_fixtures()
_FIXTURE_GEOMETRIES = _GEOMETRY_FIXTURES["geometries"]
_BASE_LON = _GEOMETRY_FIXTURES["base"]["lon"]
_BASE_LAT = _GEOMETRY_FIXTURES["base"]["lat"]

@dataclass
class TestGeometry:
    """A test geometry with metadata for validation."""

    __test__ = False

    name: str
    geojson: dict[str, Any]
    wkt: str
    geometry_type: str
    has_holes: bool = False
    is_multi: bool = False
    is_null: bool = False

    @property
    def shapely(self):
        """Get shapely geometry for validation."""
        if self.is_null:
            return None
        return shape(self.geojson)

    def to_esri_json(self) -> dict[str, Any] | None:
        """Convert to Esri JSON format for FeatureServer tests."""
        if self.is_null:
            return None
        return geojson_to_esri(self.geojson)


def geojson_to_esri(geojson: dict[str, Any]) -> dict[str, Any]:
    """Convert GeoJSON geometry to Esri JSON format."""
    geom_type = geojson.get("type", "")
    coords = geojson.get("coordinates", [])

    if geom_type == "Point":
        return {"x": coords[0], "y": coords[1], "spatialReference": {"wkid": 4326}}

    elif geom_type == "MultiPoint":
        return {
            "points": coords,
            "spatialReference": {"wkid": 4326},
        }

    elif geom_type == "LineString":
        return {
            "paths": [coords],
            "spatialReference": {"wkid": 4326},
        }

    elif geom_type == "MultiLineString":
        return {
            "paths": coords,
            "spatialReference": {"wkid": 4326},
        }

    elif geom_type == "Polygon":
        return {
            "rings": coords,
            "spatialReference": {"wkid": 4326},
        }

    elif geom_type == "MultiPolygon":
        # Flatten all rings from all polygons
        rings = []
        for polygon in coords:
            rings.extend(polygon)
        return {
            "rings": rings,
            "spatialReference": {"wkid": 4326},
        }

    elif geom_type == "GeometryCollection":
        # Esri doesn't support GeometryCollection directly
        # Return the first geometry or None
        geometries = geojson.get("geometries", [])
        if geometries:
            return geojson_to_esri(geometries[0])
        return None

    return {}


class GeometryGenerator:
    """
    Generates test geometries for comprehensive spatial coverage.

    Usage:
        gen = GeometryGenerator()
        for geom in gen.all_geometries():
            # Test with geom.geojson, geom.wkt, geom.shapely
            pass
    """

    # Base coordinates for test data (San Francisco area)
    BASE_LON = _BASE_LON
    BASE_LAT = _BASE_LAT

    def _from_fixture(self, key: str, name: str | None = None) -> TestGeometry:
        fixture = _FIXTURE_GEOMETRIES[key]
        geojson = fixture["geojson"]
        is_null = fixture.get("isNull", False) or geojson is None
        geometry_type = fixture.get("geometryType", "Unknown")
        has_holes = fixture.get("hasHoles", False)
        is_multi = fixture.get("isMulti", False)
        resolved_name = name or fixture.get("defaultName", key)

        wkt = "" if is_null else shape(geojson).wkt

        return TestGeometry(
            name=resolved_name,
            geojson=geojson,
            wkt=wkt,
            geometry_type=geometry_type,
            has_holes=has_holes,
            is_multi=is_multi,
            is_null=is_null,
        )

    def point(self, name: str = "test_point", lon: float = None, lat: float = None) -> TestGeometry:
        """Generate a Point geometry."""
        if lon is None and lat is None:
            return self._from_fixture("point", name)

        lon = lon or self.BASE_LON
        lat = lat or self.BASE_LAT
        geom = Point(lon, lat)
        return TestGeometry(
            name=name,
            geojson=mapping(geom),
            wkt=geom.wkt,
            geometry_type="Point",
        )

    def multipoint(self, name: str = "test_multipoint", count: int = 3) -> TestGeometry:
        """Generate a MultiPoint geometry."""
        if count == 3:
            return self._from_fixture("multipoint", name)

        points = [(self.BASE_LON + i * 0.01, self.BASE_LAT + i * 0.01) for i in range(count)]
        geom = MultiPoint(points)
        return TestGeometry(
            name=name,
            geojson=mapping(geom),
            wkt=geom.wkt,
            geometry_type="MultiPoint",
            is_multi=True,
        )

    def linestring(self, name: str = "test_linestring", points: int = 4) -> TestGeometry:
        """Generate a LineString geometry."""
        if points == 4:
            return self._from_fixture("linestring", name)

        coords = [(self.BASE_LON + i * 0.01, self.BASE_LAT + i * 0.005) for i in range(points)]
        geom = LineString(coords)
        return TestGeometry(
            name=name,
            geojson=mapping(geom),
            wkt=geom.wkt,
            geometry_type="LineString",
        )

    def multilinestring(self, name: str = "test_multilinestring", lines: int = 2) -> TestGeometry:
        """Generate a MultiLineString geometry."""
        if lines == 2:
            return self._from_fixture("multilinestring", name)

        all_lines = []
        for line_idx in range(lines):
            coords = [
                (self.BASE_LON + i * 0.01, self.BASE_LAT + line_idx * 0.02 + i * 0.005)
                for i in range(3)
            ]
            all_lines.append(coords)
        geom = MultiLineString(all_lines)
        return TestGeometry(
            name=name,
            geojson=mapping(geom),
            wkt=geom.wkt,
            geometry_type="MultiLineString",
            is_multi=True,
        )

    def polygon_simple(self, name: str = "test_polygon_simple") -> TestGeometry:
        """Generate a simple Polygon without holes."""
        return self._from_fixture("polygonSimple", name)

    def polygon_with_hole(self, name: str = "test_polygon_with_hole") -> TestGeometry:
        """Generate a Polygon with one hole."""
        return self._from_fixture("polygonWithHole", name)

    def polygon_with_multiple_holes(
        self, name: str = "test_polygon_multiple_holes"
    ) -> TestGeometry:
        """Generate a Polygon with multiple holes."""
        return self._from_fixture("polygonWithMultipleHoles", name)

    def multipolygon_simple(self, name: str = "test_multipolygon_simple") -> TestGeometry:
        """Generate a MultiPolygon without holes."""
        return self._from_fixture("multipolygonSimple", name)

    def multipolygon_with_holes(
        self, name: str = "test_multipolygon_with_holes"
    ) -> TestGeometry:
        """Generate a MultiPolygon with holes in some polygons."""
        return self._from_fixture("multipolygonWithHoles", name)

    def geometry_collection(self, name: str = "test_geometry_collection") -> TestGeometry:
        """Generate a GeometryCollection with mixed geometry types."""
        return self._from_fixture("geometryCollection", name)

    def null_geometry(self, name: str = "test_null_geometry") -> TestGeometry:
        """Generate a null geometry representation."""
        return self._from_fixture("nullGeometry", name)

    def all_geometries(self) -> list[TestGeometry]:
        """Generate all supported geometry types for comprehensive testing."""
        return [
            self.point(),
            self.multipoint(),
            self.linestring(),
            self.multilinestring(),
            self.polygon_simple(),
            self.polygon_with_hole(),
            self.polygon_with_multiple_holes(),
            self.multipolygon_simple(),
            self.multipolygon_with_holes(),
            self.geometry_collection(),
            self.null_geometry(),
        ]

    def points_grid(
        self,
        name_prefix: str = "grid",
        rows: int = 5,
        cols: int = 5,
        spacing: float = 0.01,
    ) -> list[TestGeometry]:
        """Generate a grid of point geometries."""
        geometries = []
        for r in range(rows):
            for c in range(cols):
                lon = self.BASE_LON + c * spacing
                lat = self.BASE_LAT + r * spacing
                name = f"{name_prefix}_{r}_{c}"
                geometries.append(self.point(name=name, lon=lon, lat=lat))
        return geometries

    def bbox(
        self, min_lon: float = None, min_lat: float = None, max_lon: float = None, max_lat: float = None
    ) -> tuple[float, float, float, float]:
        """Get a bounding box tuple (min_lon, min_lat, max_lon, max_lat)."""
        return (
            min_lon or self.BASE_LON - 0.1,
            min_lat or self.BASE_LAT - 0.1,
            max_lon or self.BASE_LON + 0.1,
            max_lat or self.BASE_LAT + 0.1,
        )


# Convenience list of all geometry type names
ALL_GEOMETRY_TYPES = _GEOMETRY_FIXTURES.get("geometryTypes", [])
