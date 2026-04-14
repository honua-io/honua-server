# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Comprehensive spatial function compliance test cases for enhanced OGC standards support.
"""

from __future__ import annotations


# Enhanced spatial function test cases per OGC specifications
ENHANCED_SPATIAL_FUNCTIONS: list[tuple[str, str, str]] = [
    # Metric functions
    ("area_calculation", "ST_Area(property_boundary) > 1000", "Spatial area calculation"),
    ("length_measurement", "ST_Length(road_segment) BETWEEN 100 AND 500", "Spatial length measurement"),
    ("perimeter_check", "ST_Perimeter(building_footprint) < 200", "Spatial perimeter calculation"),
    ("distance_between", "ST_Distance(point_a, point_b) <= 1000", "Distance between geometries"),

    # Geometric operations
    ("buffer_operation", "ST_Intersects(ST_Buffer(facility, 500), service_area)", "Buffer operation with intersection"),
    ("centroid_calculation", "ST_DWithin(location, ST_Centroid(region), 100)", "Centroid calculation"),
    ("envelope_bounds", "S_INTERSECTS(geometry, ST_Envelope(bounding_region))", "Envelope bounding box"),
    ("convex_hull", "ST_Area(ST_ConvexHull(complex_geometry)) > ST_Area(complex_geometry)", "Convex hull operation"),
    ("boundary_extraction", "ST_Length(ST_Boundary(polygon_feature)) > 0", "Boundary extraction"),

    # Geometric properties
    ("geometry_count", "ST_NumGeometries(multi_geometry) > 1", "Number of geometries in collection"),
    ("geometry_type", "ST_GeometryType(feature_geom) = 'POLYGON'", "Geometry type identification"),
    ("srid_check", "ST_SRID(coordinate_geometry) = 4326", "Spatial reference system ID"),

    # Validation functions
    ("validity_check", "ST_IsValid(user_geometry) = TRUE", "Geometry validity check"),
    ("simplicity_check", "ST_IsSimple(line_geometry) = TRUE", "Geometry simplicity check"),
    ("closed_check", "ST_IsClosed(ring_geometry) = TRUE", "Closed geometry check"),
    ("empty_check", "ST_IsEmpty(optional_geometry) = FALSE", "Empty geometry check"),
]

# Complex spatial expressions combining functions and predicates
COMPLEX_SPATIAL_EXPRESSIONS: list[tuple[str, str, str]] = [
    (
        "area_buffer_comparison",
        "ST_Area(ST_Buffer(building, 10)) > ST_Area(building) * 1.5",
        "Area comparison with buffered geometry"
    ),
    (
        "distance_from_centroid",
        "ST_Distance(point_location, ST_Centroid(service_area)) < 500",
        "Distance from centroid calculation"
    ),
    (
        "multi_function_validation",
        "ST_IsValid(geometry) = TRUE AND ST_Area(geometry) > 100 AND ST_GeometryType(geometry) = 'POLYGON'",
        "Multiple function validation"
    ),
    (
        "nested_spatial_operations",
        "S_CONTAINS(ST_Buffer(ST_Centroid(region), 1000), facility_location)",
        "Nested spatial operations"
    ),
]

# Spatial functions with different geometry types
GEOMETRY_TYPE_FUNCTIONS: list[tuple[str, str, str]] = [
    (
        "point_buffer",
        "ST_Area(ST_Buffer(POINT(1 2), 100)) > 31400",
        "Point buffer area calculation"
    ),
    (
        "linestring_length",
        "ST_Length(LINESTRING(0 0, 10 0, 10 10)) > 15",
        "LineString length calculation"
    ),
    (
        "polygon_area",
        "ST_Area(POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))) = 100",
        "Polygon area calculation"
    ),
    (
        "multipoint_count",
        "ST_NumGeometries(MULTIPOINT((0 0), (10 10), (5 5))) = 3",
        "MultiPoint geometry count"
    ),
]

# Mathematical and aggregate functions in spatial context
SPATIAL_MATH_EXPRESSIONS: list[tuple[str, str, str]] = [
    (
        "area_percentage",
        "ROUND((ST_Area(intersection) / ST_Area(total_area)) * 100, 2) > 25.0",
        "Area percentage calculation"
    ),
    (
        "distance_aggregation",
        "AVG(ST_Distance(facility, customer_location)) < 2000",
        "Average distance aggregation"
    ),
    (
        "minimum_distance",
        "MIN(ST_Distance(service_point, customer_locations)) < 500",
        "Minimum distance calculation"
    ),
    (
        "total_length",
        "SUM(ST_Length(road_segments)) > 10000",
        "Total length aggregation"
    ),
]

# All spatial function compliance test cases combined
ALL_SPATIAL_FUNCTION_CASES = (
    ENHANCED_SPATIAL_FUNCTIONS +
    COMPLEX_SPATIAL_EXPRESSIONS +
    GEOMETRY_TYPE_FUNCTIONS +
    SPATIAL_MATH_EXPRESSIONS
)