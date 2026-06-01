# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared CQL2 filter cases for integration tests.
"""

from __future__ import annotations


CQL2_TEXT_CASES: list[tuple[str, str]] = [
    ("equals_string", "name = 'alpha'"),
    ("not_equals_string", "name <> 'alpha'"),
    ("like_casei", "CASEI(name) LIKE CASEI('ALP%')"),
    ("not_like", "name NOT LIKE 'zzz%'"),
    ("in_list", "status IN ('active', 'inactive')"),
    ("not_in_list", "status NOT IN ('archived')"),
    ("between", "count BETWEEN 1 AND 5"),
    ("not_between", "count NOT BETWEEN 1 AND 5"),
    ("is_null", "description IS NULL"),
    ("is_not_null", "description IS NOT NULL"),
    ("logical_and", "count > 1 AND active = TRUE"),
    ("logical_or", "count > 100 OR status = 'active'"),
    ("arithmetic", "count + 1 > 1"),
    ("function_upper", "UPPER(name) = 'ALPHA'"),
    ("function_concat", "CONCAT(name, 'x') LIKE 'alphax'"),
    ("function_mod", "MOD(count, 2) = 0"),
    ("spatial_intersects", "S_INTERSECTS(geometry, POINT(-122.4194 37.7749))"),
    ("spatial_dwithin", "S_DWITHIN(geometry, POINT(-122.4194 37.7749), 1000)"),
    ("spatial_bbox", "S_INTERSECTS(geometry, BBOX(-122.5, 37.7, -122.4, 37.8))"),
    ("temporal_after", "T_AFTER(created_at, TIMESTAMP('2024-01-01T00:00:00Z'))"),
    ("temporal_during", "T_DURING(created_at, INTERVAL('2024-01-01T00:00:00Z', '2024-01-31T23:59:59Z'))"),
    ("array_contains", "A_CONTAINS(tags, ('red', 'blue'))"),
    ("array_overlaps", "A_OVERLAPS(numbers, (1, 2, 99))"),
]

CQL2_JSON_CASES: list[tuple[str, dict]] = [
    (
        "equals_string",
        {"op": "=", "args": [{"property": "name"}, "alpha"]},
    ),
    (
        "in_list",
        {"op": "in", "args": [{"property": "status"}, ["active", "inactive"]]},
    ),
    (
        "between",
        {"op": "between", "args": [{"property": "count"}, 1, 5]},
    ),
    (
        "like",
        {"op": "like", "args": [{"property": "name"}, "alp%"]},
    ),
    (
        "logical_and",
        {
            "op": "and",
            "args": [
                {"op": ">", "args": [{"property": "count"}, 1]},
                {"op": "=", "args": [{"property": "active"}, True]},
            ],
        },
    ),
    (
        "arithmetic",
        {"op": ">", "args": [{"op": "+", "args": [{"property": "count"}, 1]}, 1]},
    ),
    (
        "spatial_intersects",
        {
            "op": "s_intersects",
            "args": [
                {"property": "geometry"},
                {"type": "Point", "coordinates": [-122.4194, 37.7749]},
            ],
        },
    ),
    (
        "spatial_dwithin",
        {
            "op": "s_dwithin",
            "args": [
                {"property": "geometry"},
                {"type": "Point", "coordinates": [-122.4194, 37.7749]},
                1000,
            ],
        },
    ),
    (
        "temporal_after",
        {
            "op": "t_after",
            "args": [
                {"property": "created_at"},
                {"timestamp": "2024-01-01T00:00:00Z"},
            ],
        },
    ),
    (
        "array_contains",
        {
            "op": "a_contains",
            "args": [
                {"property": "tags"},
                ["red", "blue"],
            ],
        },
    ),
]
