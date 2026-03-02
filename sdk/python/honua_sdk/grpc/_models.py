"""Pythonic domain models for the Honua gRPC client.

These are NOT proto-generated; they form the public API surface that
callers interact with.  The proto adapter converts between these and
the wire-format protobuf messages.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
from typing import Any


class FieldType(IntEnum):
    UNSPECIFIED = 0
    STRING = 1
    INTEGER = 2
    BIG_INTEGER = 3
    DOUBLE = 4
    FLOAT = 5
    BOOLEAN = 6
    DATE_TIME = 7
    DATE = 8
    TIME = 9
    GEOMETRY = 10
    JSON = 11
    BINARY = 12
    UUID = 13


class GeometryType(IntEnum):
    UNSPECIFIED = 0
    POINT = 1
    MULTI_POINT = 2
    LINE_STRING = 3
    MULTI_LINE_STRING = 4
    POLYGON = 5
    MULTI_POLYGON = 6
    GEOMETRY_COLLECTION = 7
    NONE = 8


class SpatialRelationship(IntEnum):
    UNSPECIFIED = 0
    INTERSECTS = 1
    WITHIN = 2
    CONTAINS = 3
    ENVELOPE_INTERSECTS = 4
    CROSSES = 5
    TOUCHES = 6
    OVERLAPS = 7
    DISJOINT = 8
    EQUALS = 9
    WITHIN_DISTANCE = 10
    BEYOND_DISTANCE = 11
    NEAREST_NEIGHBOR = 12


class DistanceUnit(IntEnum):
    UNSPECIFIED = 0
    METERS = 1
    FEET = 2
    KILOMETERS = 3
    MILES = 4


class StatisticType(IntEnum):
    UNSPECIFIED = 0
    COUNT = 1
    SUM = 2
    MIN = 3
    MAX = 4
    AVG = 5
    STDDEV = 6
    VAR = 7


@dataclass
class SpatialReference:
    wkid: int = 0
    latest_wkid: int = 0
    wkt: str = ""


@dataclass
class Extent:
    xmin: float = 0.0
    ymin: float = 0.0
    xmax: float = 0.0
    ymax: float = 0.0
    spatial_reference: SpatialReference | None = None


@dataclass
class FieldDefinition:
    name: str = ""
    field_type: FieldType = FieldType.UNSPECIFIED
    length: int = 0
    nullable: bool = False


@dataclass
class StatisticDefinition:
    on_statistic_field: str = ""
    statistic_type: StatisticType = StatisticType.UNSPECIFIED
    out_statistic_field_name: str = ""


@dataclass
class SpatialFilter:
    geometry: dict[str, Any] | None = None  # Esri JSON geometry
    spatial_relationship: SpatialRelationship = SpatialRelationship.UNSPECIFIED
    spatial_reference: SpatialReference | None = None
    distance: float = 0.0
    distance_unit: DistanceUnit = DistanceUnit.UNSPECIFIED
    nearest_count: int = 0
    return_distance: bool = False


@dataclass
class QueryFeaturesRequest:
    service_id: str = ""
    layer_id: int = 0
    where: str = "1=1"
    object_ids: list[int] | None = None
    out_fields: list[str] | None = None
    return_geometry: bool = True
    out_sr: SpatialReference | None = None
    result_offset: int = 0
    result_record_count: int = 0
    order_by: str = ""
    return_distinct: bool = False
    return_count_only: bool = False
    return_ids_only: bool = False
    return_extent_only: bool = False
    out_statistics: list[StatisticDefinition] | None = None
    group_by: list[str] | None = None
    geometry_precision: int = 0
    max_allowable_offset: float = 0.0
    spatial_filter: SpatialFilter | None = None


@dataclass
class Feature:
    id: int = 0
    attributes: dict[str, Any] = field(default_factory=dict)
    geometry: dict[str, Any] | None = None  # Esri JSON shape


@dataclass(frozen=True, slots=True)
class QueryFeaturesResponse:
    object_id_field_name: str = ""
    geometry_type: GeometryType = GeometryType.UNSPECIFIED
    spatial_reference: SpatialReference | None = None
    fields: list[FieldDefinition] = field(default_factory=list)
    features: list[Feature] = field(default_factory=list)
    exceeded_transfer_limit: bool = False
    count: int = 0
    object_ids: list[int] = field(default_factory=list)
    extent: Extent | None = None


@dataclass(frozen=True, slots=True)
class FeaturePage:
    object_id_field_name: str = ""
    geometry_type: GeometryType = GeometryType.UNSPECIFIED
    spatial_reference: SpatialReference | None = None
    fields: list[FieldDefinition] = field(default_factory=list)
    features: list[Feature] = field(default_factory=list)
    is_last_page: bool = False
