from google.protobuf.internal import containers as _containers
from google.protobuf.internal import enum_type_wrapper as _enum_type_wrapper
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from typing import ClassVar as _ClassVar, Iterable as _Iterable, Mapping as _Mapping, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class NullValue(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    NULL_VALUE: _ClassVar[NullValue]

class FieldType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    FIELD_TYPE_UNSPECIFIED: _ClassVar[FieldType]
    FIELD_TYPE_STRING: _ClassVar[FieldType]
    FIELD_TYPE_INTEGER: _ClassVar[FieldType]
    FIELD_TYPE_BIG_INTEGER: _ClassVar[FieldType]
    FIELD_TYPE_DOUBLE: _ClassVar[FieldType]
    FIELD_TYPE_FLOAT: _ClassVar[FieldType]
    FIELD_TYPE_BOOLEAN: _ClassVar[FieldType]
    FIELD_TYPE_DATE_TIME: _ClassVar[FieldType]
    FIELD_TYPE_DATE: _ClassVar[FieldType]
    FIELD_TYPE_TIME: _ClassVar[FieldType]
    FIELD_TYPE_GEOMETRY: _ClassVar[FieldType]
    FIELD_TYPE_JSON: _ClassVar[FieldType]
    FIELD_TYPE_BINARY: _ClassVar[FieldType]
    FIELD_TYPE_UUID: _ClassVar[FieldType]

class GeometryType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    GEOMETRY_TYPE_UNSPECIFIED: _ClassVar[GeometryType]
    GEOMETRY_TYPE_POINT: _ClassVar[GeometryType]
    GEOMETRY_TYPE_MULTI_POINT: _ClassVar[GeometryType]
    GEOMETRY_TYPE_LINE_STRING: _ClassVar[GeometryType]
    GEOMETRY_TYPE_MULTI_LINE_STRING: _ClassVar[GeometryType]
    GEOMETRY_TYPE_POLYGON: _ClassVar[GeometryType]
    GEOMETRY_TYPE_MULTI_POLYGON: _ClassVar[GeometryType]
    GEOMETRY_TYPE_GEOMETRY_COLLECTION: _ClassVar[GeometryType]
    GEOMETRY_TYPE_NONE: _ClassVar[GeometryType]

class SpatialRelationship(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    SPATIAL_RELATIONSHIP_UNSPECIFIED: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_INTERSECTS: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_WITHIN: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_CONTAINS: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_ENVELOPE_INTERSECTS: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_CROSSES: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_TOUCHES: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_OVERLAPS: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_DISJOINT: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_EQUALS: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_WITHIN_DISTANCE: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_BEYOND_DISTANCE: _ClassVar[SpatialRelationship]
    SPATIAL_RELATIONSHIP_NEAREST_NEIGHBOR: _ClassVar[SpatialRelationship]

class DistanceUnit(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    DISTANCE_UNIT_UNSPECIFIED: _ClassVar[DistanceUnit]
    DISTANCE_UNIT_METERS: _ClassVar[DistanceUnit]
    DISTANCE_UNIT_FEET: _ClassVar[DistanceUnit]
    DISTANCE_UNIT_KILOMETERS: _ClassVar[DistanceUnit]
    DISTANCE_UNIT_MILES: _ClassVar[DistanceUnit]

class StatisticType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    STATISTIC_TYPE_UNSPECIFIED: _ClassVar[StatisticType]
    STATISTIC_TYPE_COUNT: _ClassVar[StatisticType]
    STATISTIC_TYPE_SUM: _ClassVar[StatisticType]
    STATISTIC_TYPE_MIN: _ClassVar[StatisticType]
    STATISTIC_TYPE_MAX: _ClassVar[StatisticType]
    STATISTIC_TYPE_AVG: _ClassVar[StatisticType]
    STATISTIC_TYPE_STDDEV: _ClassVar[StatisticType]
    STATISTIC_TYPE_VAR: _ClassVar[StatisticType]
NULL_VALUE: NullValue
FIELD_TYPE_UNSPECIFIED: FieldType
FIELD_TYPE_STRING: FieldType
FIELD_TYPE_INTEGER: FieldType
FIELD_TYPE_BIG_INTEGER: FieldType
FIELD_TYPE_DOUBLE: FieldType
FIELD_TYPE_FLOAT: FieldType
FIELD_TYPE_BOOLEAN: FieldType
FIELD_TYPE_DATE_TIME: FieldType
FIELD_TYPE_DATE: FieldType
FIELD_TYPE_TIME: FieldType
FIELD_TYPE_GEOMETRY: FieldType
FIELD_TYPE_JSON: FieldType
FIELD_TYPE_BINARY: FieldType
FIELD_TYPE_UUID: FieldType
GEOMETRY_TYPE_UNSPECIFIED: GeometryType
GEOMETRY_TYPE_POINT: GeometryType
GEOMETRY_TYPE_MULTI_POINT: GeometryType
GEOMETRY_TYPE_LINE_STRING: GeometryType
GEOMETRY_TYPE_MULTI_LINE_STRING: GeometryType
GEOMETRY_TYPE_POLYGON: GeometryType
GEOMETRY_TYPE_MULTI_POLYGON: GeometryType
GEOMETRY_TYPE_GEOMETRY_COLLECTION: GeometryType
GEOMETRY_TYPE_NONE: GeometryType
SPATIAL_RELATIONSHIP_UNSPECIFIED: SpatialRelationship
SPATIAL_RELATIONSHIP_INTERSECTS: SpatialRelationship
SPATIAL_RELATIONSHIP_WITHIN: SpatialRelationship
SPATIAL_RELATIONSHIP_CONTAINS: SpatialRelationship
SPATIAL_RELATIONSHIP_ENVELOPE_INTERSECTS: SpatialRelationship
SPATIAL_RELATIONSHIP_CROSSES: SpatialRelationship
SPATIAL_RELATIONSHIP_TOUCHES: SpatialRelationship
SPATIAL_RELATIONSHIP_OVERLAPS: SpatialRelationship
SPATIAL_RELATIONSHIP_DISJOINT: SpatialRelationship
SPATIAL_RELATIONSHIP_EQUALS: SpatialRelationship
SPATIAL_RELATIONSHIP_WITHIN_DISTANCE: SpatialRelationship
SPATIAL_RELATIONSHIP_BEYOND_DISTANCE: SpatialRelationship
SPATIAL_RELATIONSHIP_NEAREST_NEIGHBOR: SpatialRelationship
DISTANCE_UNIT_UNSPECIFIED: DistanceUnit
DISTANCE_UNIT_METERS: DistanceUnit
DISTANCE_UNIT_FEET: DistanceUnit
DISTANCE_UNIT_KILOMETERS: DistanceUnit
DISTANCE_UNIT_MILES: DistanceUnit
STATISTIC_TYPE_UNSPECIFIED: StatisticType
STATISTIC_TYPE_COUNT: StatisticType
STATISTIC_TYPE_SUM: StatisticType
STATISTIC_TYPE_MIN: StatisticType
STATISTIC_TYPE_MAX: StatisticType
STATISTIC_TYPE_AVG: StatisticType
STATISTIC_TYPE_STDDEV: StatisticType
STATISTIC_TYPE_VAR: StatisticType

class QueryFeaturesRequest(_message.Message):
    __slots__ = ("service_id", "layer_id", "where", "object_ids", "out_fields", "return_geometry", "out_sr", "result_offset", "result_record_count", "order_by", "return_distinct", "return_count_only", "return_ids_only", "return_extent_only", "out_statistics", "group_by", "geometry_precision", "max_allowable_offset", "spatial_filter")
    SERVICE_ID_FIELD_NUMBER: _ClassVar[int]
    LAYER_ID_FIELD_NUMBER: _ClassVar[int]
    WHERE_FIELD_NUMBER: _ClassVar[int]
    OBJECT_IDS_FIELD_NUMBER: _ClassVar[int]
    OUT_FIELDS_FIELD_NUMBER: _ClassVar[int]
    RETURN_GEOMETRY_FIELD_NUMBER: _ClassVar[int]
    OUT_SR_FIELD_NUMBER: _ClassVar[int]
    RESULT_OFFSET_FIELD_NUMBER: _ClassVar[int]
    RESULT_RECORD_COUNT_FIELD_NUMBER: _ClassVar[int]
    ORDER_BY_FIELD_NUMBER: _ClassVar[int]
    RETURN_DISTINCT_FIELD_NUMBER: _ClassVar[int]
    RETURN_COUNT_ONLY_FIELD_NUMBER: _ClassVar[int]
    RETURN_IDS_ONLY_FIELD_NUMBER: _ClassVar[int]
    RETURN_EXTENT_ONLY_FIELD_NUMBER: _ClassVar[int]
    OUT_STATISTICS_FIELD_NUMBER: _ClassVar[int]
    GROUP_BY_FIELD_NUMBER: _ClassVar[int]
    GEOMETRY_PRECISION_FIELD_NUMBER: _ClassVar[int]
    MAX_ALLOWABLE_OFFSET_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_FILTER_FIELD_NUMBER: _ClassVar[int]
    service_id: str
    layer_id: int
    where: str
    object_ids: _containers.RepeatedScalarFieldContainer[int]
    out_fields: _containers.RepeatedScalarFieldContainer[str]
    return_geometry: bool
    out_sr: SpatialReference
    result_offset: int
    result_record_count: int
    order_by: str
    return_distinct: bool
    return_count_only: bool
    return_ids_only: bool
    return_extent_only: bool
    out_statistics: _containers.RepeatedCompositeFieldContainer[StatisticDefinition]
    group_by: _containers.RepeatedScalarFieldContainer[str]
    geometry_precision: int
    max_allowable_offset: float
    spatial_filter: SpatialFilter
    def __init__(self, service_id: _Optional[str] = ..., layer_id: _Optional[int] = ..., where: _Optional[str] = ..., object_ids: _Optional[_Iterable[int]] = ..., out_fields: _Optional[_Iterable[str]] = ..., return_geometry: bool = ..., out_sr: _Optional[_Union[SpatialReference, _Mapping]] = ..., result_offset: _Optional[int] = ..., result_record_count: _Optional[int] = ..., order_by: _Optional[str] = ..., return_distinct: bool = ..., return_count_only: bool = ..., return_ids_only: bool = ..., return_extent_only: bool = ..., out_statistics: _Optional[_Iterable[_Union[StatisticDefinition, _Mapping]]] = ..., group_by: _Optional[_Iterable[str]] = ..., geometry_precision: _Optional[int] = ..., max_allowable_offset: _Optional[float] = ..., spatial_filter: _Optional[_Union[SpatialFilter, _Mapping]] = ...) -> None: ...

class QueryFeaturesResponse(_message.Message):
    __slots__ = ("object_id_field_name", "geometry_type", "spatial_reference", "fields", "features", "exceeded_transfer_limit", "count", "object_ids", "extent")
    OBJECT_ID_FIELD_NAME_FIELD_NUMBER: _ClassVar[int]
    GEOMETRY_TYPE_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_REFERENCE_FIELD_NUMBER: _ClassVar[int]
    FIELDS_FIELD_NUMBER: _ClassVar[int]
    FEATURES_FIELD_NUMBER: _ClassVar[int]
    EXCEEDED_TRANSFER_LIMIT_FIELD_NUMBER: _ClassVar[int]
    COUNT_FIELD_NUMBER: _ClassVar[int]
    OBJECT_IDS_FIELD_NUMBER: _ClassVar[int]
    EXTENT_FIELD_NUMBER: _ClassVar[int]
    object_id_field_name: str
    geometry_type: GeometryType
    spatial_reference: SpatialReference
    fields: _containers.RepeatedCompositeFieldContainer[FieldDefinition]
    features: _containers.RepeatedCompositeFieldContainer[Feature]
    exceeded_transfer_limit: bool
    count: int
    object_ids: _containers.RepeatedScalarFieldContainer[int]
    extent: Extent
    def __init__(self, object_id_field_name: _Optional[str] = ..., geometry_type: _Optional[_Union[GeometryType, str]] = ..., spatial_reference: _Optional[_Union[SpatialReference, _Mapping]] = ..., fields: _Optional[_Iterable[_Union[FieldDefinition, _Mapping]]] = ..., features: _Optional[_Iterable[_Union[Feature, _Mapping]]] = ..., exceeded_transfer_limit: bool = ..., count: _Optional[int] = ..., object_ids: _Optional[_Iterable[int]] = ..., extent: _Optional[_Union[Extent, _Mapping]] = ...) -> None: ...

class FeaturePage(_message.Message):
    __slots__ = ("object_id_field_name", "geometry_type", "spatial_reference", "fields", "features", "is_last_page")
    OBJECT_ID_FIELD_NAME_FIELD_NUMBER: _ClassVar[int]
    GEOMETRY_TYPE_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_REFERENCE_FIELD_NUMBER: _ClassVar[int]
    FIELDS_FIELD_NUMBER: _ClassVar[int]
    FEATURES_FIELD_NUMBER: _ClassVar[int]
    IS_LAST_PAGE_FIELD_NUMBER: _ClassVar[int]
    object_id_field_name: str
    geometry_type: GeometryType
    spatial_reference: SpatialReference
    fields: _containers.RepeatedCompositeFieldContainer[FieldDefinition]
    features: _containers.RepeatedCompositeFieldContainer[Feature]
    is_last_page: bool
    def __init__(self, object_id_field_name: _Optional[str] = ..., geometry_type: _Optional[_Union[GeometryType, str]] = ..., spatial_reference: _Optional[_Union[SpatialReference, _Mapping]] = ..., fields: _Optional[_Iterable[_Union[FieldDefinition, _Mapping]]] = ..., features: _Optional[_Iterable[_Union[Feature, _Mapping]]] = ..., is_last_page: bool = ...) -> None: ...

class Feature(_message.Message):
    __slots__ = ("id", "attributes", "geometry")
    class AttributesEntry(_message.Message):
        __slots__ = ("key", "value")
        KEY_FIELD_NUMBER: _ClassVar[int]
        VALUE_FIELD_NUMBER: _ClassVar[int]
        key: str
        value: AttributeValue
        def __init__(self, key: _Optional[str] = ..., value: _Optional[_Union[AttributeValue, _Mapping]] = ...) -> None: ...
    ID_FIELD_NUMBER: _ClassVar[int]
    ATTRIBUTES_FIELD_NUMBER: _ClassVar[int]
    GEOMETRY_FIELD_NUMBER: _ClassVar[int]
    id: int
    attributes: _containers.MessageMap[str, AttributeValue]
    geometry: Geometry
    def __init__(self, id: _Optional[int] = ..., attributes: _Optional[_Mapping[str, AttributeValue]] = ..., geometry: _Optional[_Union[Geometry, _Mapping]] = ...) -> None: ...

class AttributeValue(_message.Message):
    __slots__ = ("string_value", "int32_value", "int64_value", "double_value", "float_value", "bool_value", "datetime_value", "bytes_value", "null_value")
    STRING_VALUE_FIELD_NUMBER: _ClassVar[int]
    INT32_VALUE_FIELD_NUMBER: _ClassVar[int]
    INT64_VALUE_FIELD_NUMBER: _ClassVar[int]
    DOUBLE_VALUE_FIELD_NUMBER: _ClassVar[int]
    FLOAT_VALUE_FIELD_NUMBER: _ClassVar[int]
    BOOL_VALUE_FIELD_NUMBER: _ClassVar[int]
    DATETIME_VALUE_FIELD_NUMBER: _ClassVar[int]
    BYTES_VALUE_FIELD_NUMBER: _ClassVar[int]
    NULL_VALUE_FIELD_NUMBER: _ClassVar[int]
    string_value: str
    int32_value: int
    int64_value: int
    double_value: float
    float_value: float
    bool_value: bool
    datetime_value: int
    bytes_value: bytes
    null_value: NullValue
    def __init__(self, string_value: _Optional[str] = ..., int32_value: _Optional[int] = ..., int64_value: _Optional[int] = ..., double_value: _Optional[float] = ..., float_value: _Optional[float] = ..., bool_value: bool = ..., datetime_value: _Optional[int] = ..., bytes_value: _Optional[bytes] = ..., null_value: _Optional[_Union[NullValue, str]] = ...) -> None: ...

class Geometry(_message.Message):
    __slots__ = ("point", "multi_point", "polyline", "polygon", "multi_polygon")
    POINT_FIELD_NUMBER: _ClassVar[int]
    MULTI_POINT_FIELD_NUMBER: _ClassVar[int]
    POLYLINE_FIELD_NUMBER: _ClassVar[int]
    POLYGON_FIELD_NUMBER: _ClassVar[int]
    MULTI_POLYGON_FIELD_NUMBER: _ClassVar[int]
    point: PointGeometry
    multi_point: MultiPointGeometry
    polyline: PolylineGeometry
    polygon: PolygonGeometry
    multi_polygon: MultiPolygonGeometry
    def __init__(self, point: _Optional[_Union[PointGeometry, _Mapping]] = ..., multi_point: _Optional[_Union[MultiPointGeometry, _Mapping]] = ..., polyline: _Optional[_Union[PolylineGeometry, _Mapping]] = ..., polygon: _Optional[_Union[PolygonGeometry, _Mapping]] = ..., multi_polygon: _Optional[_Union[MultiPolygonGeometry, _Mapping]] = ...) -> None: ...

class PointGeometry(_message.Message):
    __slots__ = ("x", "y", "z", "m")
    X_FIELD_NUMBER: _ClassVar[int]
    Y_FIELD_NUMBER: _ClassVar[int]
    Z_FIELD_NUMBER: _ClassVar[int]
    M_FIELD_NUMBER: _ClassVar[int]
    x: float
    y: float
    z: float
    m: float
    def __init__(self, x: _Optional[float] = ..., y: _Optional[float] = ..., z: _Optional[float] = ..., m: _Optional[float] = ...) -> None: ...

class MultiPointGeometry(_message.Message):
    __slots__ = ("points",)
    POINTS_FIELD_NUMBER: _ClassVar[int]
    points: _containers.RepeatedCompositeFieldContainer[PointGeometry]
    def __init__(self, points: _Optional[_Iterable[_Union[PointGeometry, _Mapping]]] = ...) -> None: ...

class Coordinate(_message.Message):
    __slots__ = ("x", "y", "z", "m")
    X_FIELD_NUMBER: _ClassVar[int]
    Y_FIELD_NUMBER: _ClassVar[int]
    Z_FIELD_NUMBER: _ClassVar[int]
    M_FIELD_NUMBER: _ClassVar[int]
    x: float
    y: float
    z: float
    m: float
    def __init__(self, x: _Optional[float] = ..., y: _Optional[float] = ..., z: _Optional[float] = ..., m: _Optional[float] = ...) -> None: ...

class CoordinateSequence(_message.Message):
    __slots__ = ("coords",)
    COORDS_FIELD_NUMBER: _ClassVar[int]
    coords: _containers.RepeatedCompositeFieldContainer[Coordinate]
    def __init__(self, coords: _Optional[_Iterable[_Union[Coordinate, _Mapping]]] = ...) -> None: ...

class PolylineGeometry(_message.Message):
    __slots__ = ("paths",)
    PATHS_FIELD_NUMBER: _ClassVar[int]
    paths: _containers.RepeatedCompositeFieldContainer[CoordinateSequence]
    def __init__(self, paths: _Optional[_Iterable[_Union[CoordinateSequence, _Mapping]]] = ...) -> None: ...

class PolygonGeometry(_message.Message):
    __slots__ = ("rings",)
    RINGS_FIELD_NUMBER: _ClassVar[int]
    rings: _containers.RepeatedCompositeFieldContainer[CoordinateSequence]
    def __init__(self, rings: _Optional[_Iterable[_Union[CoordinateSequence, _Mapping]]] = ...) -> None: ...

class MultiPolygonGeometry(_message.Message):
    __slots__ = ("polygons",)
    POLYGONS_FIELD_NUMBER: _ClassVar[int]
    polygons: _containers.RepeatedCompositeFieldContainer[PolygonGeometry]
    def __init__(self, polygons: _Optional[_Iterable[_Union[PolygonGeometry, _Mapping]]] = ...) -> None: ...

class SpatialReference(_message.Message):
    __slots__ = ("wkid", "latest_wkid", "wkt")
    WKID_FIELD_NUMBER: _ClassVar[int]
    LATEST_WKID_FIELD_NUMBER: _ClassVar[int]
    WKT_FIELD_NUMBER: _ClassVar[int]
    wkid: int
    latest_wkid: int
    wkt: str
    def __init__(self, wkid: _Optional[int] = ..., latest_wkid: _Optional[int] = ..., wkt: _Optional[str] = ...) -> None: ...

class FieldDefinition(_message.Message):
    __slots__ = ("name", "field_type", "length", "nullable")
    NAME_FIELD_NUMBER: _ClassVar[int]
    FIELD_TYPE_FIELD_NUMBER: _ClassVar[int]
    LENGTH_FIELD_NUMBER: _ClassVar[int]
    NULLABLE_FIELD_NUMBER: _ClassVar[int]
    name: str
    field_type: FieldType
    length: int
    nullable: bool
    def __init__(self, name: _Optional[str] = ..., field_type: _Optional[_Union[FieldType, str]] = ..., length: _Optional[int] = ..., nullable: bool = ...) -> None: ...

class SpatialFilter(_message.Message):
    __slots__ = ("geometry", "spatial_relationship", "spatial_reference", "distance", "distance_unit", "nearest_count", "return_distance")
    GEOMETRY_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_RELATIONSHIP_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_REFERENCE_FIELD_NUMBER: _ClassVar[int]
    DISTANCE_FIELD_NUMBER: _ClassVar[int]
    DISTANCE_UNIT_FIELD_NUMBER: _ClassVar[int]
    NEAREST_COUNT_FIELD_NUMBER: _ClassVar[int]
    RETURN_DISTANCE_FIELD_NUMBER: _ClassVar[int]
    geometry: Geometry
    spatial_relationship: SpatialRelationship
    spatial_reference: SpatialReference
    distance: float
    distance_unit: DistanceUnit
    nearest_count: int
    return_distance: bool
    def __init__(self, geometry: _Optional[_Union[Geometry, _Mapping]] = ..., spatial_relationship: _Optional[_Union[SpatialRelationship, str]] = ..., spatial_reference: _Optional[_Union[SpatialReference, _Mapping]] = ..., distance: _Optional[float] = ..., distance_unit: _Optional[_Union[DistanceUnit, str]] = ..., nearest_count: _Optional[int] = ..., return_distance: bool = ...) -> None: ...

class StatisticDefinition(_message.Message):
    __slots__ = ("on_statistic_field", "statistic_type", "out_statistic_field_name")
    ON_STATISTIC_FIELD_FIELD_NUMBER: _ClassVar[int]
    STATISTIC_TYPE_FIELD_NUMBER: _ClassVar[int]
    OUT_STATISTIC_FIELD_NAME_FIELD_NUMBER: _ClassVar[int]
    on_statistic_field: str
    statistic_type: StatisticType
    out_statistic_field_name: str
    def __init__(self, on_statistic_field: _Optional[str] = ..., statistic_type: _Optional[_Union[StatisticType, str]] = ..., out_statistic_field_name: _Optional[str] = ...) -> None: ...

class Extent(_message.Message):
    __slots__ = ("xmin", "ymin", "xmax", "ymax", "spatial_reference")
    XMIN_FIELD_NUMBER: _ClassVar[int]
    YMIN_FIELD_NUMBER: _ClassVar[int]
    XMAX_FIELD_NUMBER: _ClassVar[int]
    YMAX_FIELD_NUMBER: _ClassVar[int]
    SPATIAL_REFERENCE_FIELD_NUMBER: _ClassVar[int]
    xmin: float
    ymin: float
    xmax: float
    ymax: float
    spatial_reference: SpatialReference
    def __init__(self, xmin: _Optional[float] = ..., ymin: _Optional[float] = ..., xmax: _Optional[float] = ..., ymax: _Optional[float] = ..., spatial_reference: _Optional[_Union[SpatialReference, _Mapping]] = ...) -> None: ...
