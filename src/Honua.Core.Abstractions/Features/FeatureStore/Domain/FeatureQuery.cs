// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a query specification for features
/// </summary>
public readonly record struct FeatureQuery
{
    /// <summary>
    /// WHERE clause filter expression (GeoServices REST SQL syntax)
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Parameterized SQL filter with parameters (takes precedence over Where if provided)
    /// </summary>
    public SqlFragment? SqlFilter { get; init; }

    /// <summary>
    /// Provider-enforced SQL filter applied before caller-supplied filters.
    /// </summary>
    public SqlFragment? EnforcedSqlFilter { get; init; }

    /// <summary>
    /// Optional list of object IDs to filter by
    /// </summary>
    public ImmutableArray<long>? ObjectIds { get; init; }

    /// <summary>
    /// Fields to return (null means all fields)
    /// </summary>
    public ImmutableArray<string>? OutFields { get; init; }

    /// <summary>
    /// Excludes the JSON attribute payload entirely when the caller only needs geometry/system columns.
    /// </summary>
    public bool ExcludeAttributes { get; init; }

    /// <summary>
    /// Spatial filter for geometry-based queries
    /// </summary>
    public SpatialFilter? SpatialFilter { get; init; }

    /// <summary>
    /// Geometry type of the target layer when known.
    /// </summary>
    public MetadataV2GeometryType? GeometryType { get; init; }

    /// <summary>
    /// Attribute field that should be promoted to the GeoJSON feature id when raw JSON
    /// fast paths can project it directly.
    /// </summary>
    public string? PublicIdAttributeName { get; init; }

    /// <summary>
    /// SRID of the stored layer geometry (used for spatial filter transforms and output CRS handling)
    /// </summary>
    public int? SpatialReferenceSrid { get; init; }

    /// <summary>
    /// Optional output SRID for geometry transformation in query results
    /// </summary>
    public int? OutputSrid { get; init; }

    /// <summary>
    /// Optional output axis order for formats that encode coordinates according to CRS axis definitions.
    /// </summary>
    public AxisOrder? OutputAxisOrder { get; init; }

    /// <summary>
    /// Optional projected point grid used by raster rendering fast paths to thin
    /// point results before they are materialized by the application.
    /// </summary>
    public RasterPointGrid? RasterPointGrid { get; init; }

    /// <summary>
    /// Temporal filter for time-based queries
    /// </summary>
    public TemporalFilter? TemporalFilter { get; init; }

    /// <summary>
    /// Include features without geometry when a spatial filter is provided
    /// </summary>
    public bool IncludeNullGeometry { get; init; }

    /// <summary>
    /// Number of records to skip for pagination
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Backward-compatible alias for REST-style paging.
    /// </summary>
    public int? ResultOffset
    {
        readonly get => Offset;
        init => Offset = value;
    }

    /// <summary>
    /// Maximum number of records to return
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Backward-compatible alias for REST-style paging.
    /// </summary>
    public int? ResultRecordCount
    {
        readonly get => Limit;
        init => Limit = value;
    }

    /// <summary>
    /// Order by clauses for sorting results (e.g., "name asc", "population desc")
    /// </summary>
    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

    /// <summary>
    /// Whether to return only distinct rows (SQL DISTINCT)
    /// </summary>
    public bool Distinct { get; init; }

    /// <summary>
    /// Aggregate statistic definitions for statistics queries (outStatistics)
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Fields to group by for statistics queries (groupByFieldsForStatistics)
    /// </summary>
    public ImmutableArray<string>? GroupByFields { get; init; }

    /// <summary>
    /// Aggregate filter conditions applied after grouping (the SQL <c>HAVING</c>
    /// clause). Each condition is a structured aggregate comparison; the provider
    /// rebuilds and parameterizes the clause so no raw SQL is passed through.
    /// Only meaningful alongside <see cref="OutStatistics"/>.
    /// </summary>
    public ImmutableArray<HavingCondition>? Having { get; init; }

    /// <summary>
    /// Top features filter for queryTopFeatures operations (window-function partitioning)
    /// </summary>
    public TopFilter? TopFilter { get; init; }

    /// <summary>
    /// Creates a simple WHERE clause query
    /// </summary>
    /// <param name="where">WHERE clause expression</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithWhere(string where)
        => new() { Where = where };

    /// <summary>
    /// Creates a parameterized SQL filter query
    /// </summary>
    /// <param name="sqlFilter">Parameterized SQL filter with parameters</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithSqlFilter(SqlFragment sqlFilter)
        => new() { SqlFilter = sqlFilter };

    /// <summary>
    /// Creates a query with pagination
    /// </summary>
    /// <param name="offset">Number of records to skip</param>
    /// <param name="limit">Maximum number of records</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithPaging(int offset, int limit)
        => new() { Offset = offset, Limit = limit };

    /// <summary>
    /// Creates a spatial query
    /// </summary>
    /// <param name="spatialFilter">Spatial filter specification</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithSpatialFilter(SpatialFilter spatialFilter)
        => new() { SpatialFilter = spatialFilter };
}

/// <summary>
/// Temporal filtering criteria
/// </summary>
public readonly record struct TemporalFilter
{
    /// <summary>
    /// Name of the temporal property to filter on. For interval-style layers
    /// this is the start time field; pair with <see cref="EndPropertyName"/>
    /// to filter on the configured interval rather than just the start instant.
    /// </summary>
    public required string PropertyName { get; init; }

    /// <summary>
    /// Type of the temporal property
    /// </summary>
    public required TemporalPropertyType PropertyType { get; init; }

    /// <summary>
    /// Optional end-time property for interval-style layers. When set, filters
    /// apply interval-intersection semantics — the row's interval is
    /// <c>[PropertyName, COALESCE(EndPropertyName, PropertyName)]</c> and
    /// matches when it overlaps the requested <c>[Start, End]</c> window.
    /// Mirrors the GeoServices REST <c>query?time=</c> behavior so render and
    /// query paths share semantics. Null for instant-only layers.
    /// </summary>
    public string? EndPropertyName { get; init; }

    /// <summary>
    /// Inclusive start of the temporal interval (null for open start)
    /// </summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>
    /// Inclusive end of the temporal interval (null for open end)
    /// </summary>
    public DateTimeOffset? End { get; init; }
}

/// <summary>
/// Temporal property type for filtering
/// </summary>
public enum TemporalPropertyType
{
    /// <summary>
    /// DateTime values with time and timezone information
    /// </summary>
    DateTime,

    /// <summary>
    /// Date values without time component
    /// </summary>
    Date
}

/// <summary>
/// Represents spatial filtering criteria
/// </summary>
public readonly record struct SpatialFilter
{
    /// <summary>
    /// Geometry for spatial filtering in Well-Known Binary (WKB) format
    /// </summary>
    public required byte[] Geometry { get; init; }

    /// <summary>
    /// SRID of the filter geometry (null if unspecified)
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Spatial relationship type
    /// </summary>
    public required SpatialRelationship SpatialRelationship { get; init; }

    /// <summary>
    /// Distance value for distance-based spatial queries (WithinDistance, BeyondDistance).
    /// The unit is determined by the DistanceUnit property.
    /// </summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Unit for distance measurements. Defaults to Meters.
    /// </summary>
    public DistanceUnit DistanceUnit { get; init; }

    /// <summary>
    /// Whether the filter geometry is a simple axis-aligned envelope.
    /// </summary>
    public bool IsSimpleEnvelope { get; init; }

    /// <summary>
    /// Whether point envelope filters may use an envelope-only predicate. This is intended
    /// for viewport/display bbox requests where tiny edge candidates are acceptable.
    /// </summary>
    public bool AllowEnvelopeOnly { get; init; }

    /// <summary>
    /// Minimum X coordinate when the filter is a simple envelope.
    /// </summary>
    public double? EnvelopeMinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate when the filter is a simple envelope.
    /// </summary>
    public double? EnvelopeMinY { get; init; }

    /// <summary>
    /// Maximum X coordinate when the filter is a simple envelope.
    /// </summary>
    public double? EnvelopeMaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate when the filter is a simple envelope.
    /// </summary>
    public double? EnvelopeMaxY { get; init; }

    /// <summary>
    /// Number of nearest neighbors to return for KNN queries.
    /// Only applicable when SpatialRelationship is NearestNeighbor.
    /// </summary>
    public int? NearestCount { get; init; }

    /// <summary>
    /// Whether to include the computed distance value in results for KNN queries.
    /// </summary>
    public bool ReturnDistance { get; init; }

    /// <summary>
    /// Creates a spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="spatialRelationship">Type of spatial relationship</param>
    /// <param name="srid">SRID of the filter geometry</param>
    /// <param name="isSimpleEnvelope">Whether the filter geometry is a simple axis-aligned envelope</param>
    /// <param name="allowEnvelopeOnly">Whether point envelope filters may use an envelope-only predicate</param>
    /// <param name="envelopeMinX">Minimum X coordinate when the filter is a simple envelope</param>
    /// <param name="envelopeMinY">Minimum Y coordinate when the filter is a simple envelope</param>
    /// <param name="envelopeMaxX">Maximum X coordinate when the filter is a simple envelope</param>
    /// <param name="envelopeMaxY">Maximum Y coordinate when the filter is a simple envelope</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter Create(
        byte[] geometry,
        SpatialRelationship spatialRelationship,
        int? srid = null,
        bool isSimpleEnvelope = false,
        bool allowEnvelopeOnly = false,
        double? envelopeMinX = null,
        double? envelopeMinY = null,
        double? envelopeMaxX = null,
        double? envelopeMaxY = null)
        => new()
        {
            Geometry = geometry,
            SpatialRelationship = spatialRelationship,
            Srid = srid,
            IsSimpleEnvelope = isSimpleEnvelope,
            AllowEnvelopeOnly = allowEnvelopeOnly,
            EnvelopeMinX = envelopeMinX,
            EnvelopeMinY = envelopeMinY,
            EnvelopeMaxX = envelopeMaxX,
            EnvelopeMaxY = envelopeMaxY
        };

    /// <summary>
    /// Creates a distance-based spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="distance">Distance value</param>
    /// <param name="unit">Distance unit (defaults to Meters)</param>
    /// <param name="withinDistance">True for within distance, false for beyond distance</param>
    /// <param name="srid">SRID of the filter geometry</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter CreateDistanceFilter(
        byte[] geometry,
        double distance,
        DistanceUnit unit = DistanceUnit.Meters,
        bool withinDistance = true,
        int? srid = null)
        => new()
        {
            Geometry = geometry,
            Srid = srid,
            SpatialRelationship = withinDistance ? SpatialRelationship.WithinDistance : SpatialRelationship.BeyondDistance,
            Distance = distance,
            DistanceUnit = unit
        };

    /// <summary>
    /// Creates a K-Nearest Neighbor (KNN) spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="count">Number of nearest neighbors to return</param>
    /// <param name="returnDistance">Whether to include distance values in results</param>
    /// <param name="srid">SRID of the filter geometry</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter CreateKnnFilter(byte[] geometry, int count, bool returnDistance = false, int? srid = null)
        => new()
        {
            Geometry = geometry,
            Srid = srid,
            SpatialRelationship = SpatialRelationship.NearestNeighbor,
            NearestCount = count,
            ReturnDistance = returnDistance
        };
}

/// <summary>
/// Represents an order by clause for sorting results
/// </summary>
public readonly record struct OrderByClause
{
    /// <summary>
    /// Field name to sort by
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Sort direction (true = ascending, false = descending)
    /// </summary>
    public bool Ascending { get; init; } = true;

    /// <summary>
    /// Field type metadata for typed ordering (null when unknown)
    /// </summary>
    public MetadataV2FieldType? FieldType { get; init; }

    /// <summary>
    /// Initializes a new instance of the OrderByClause struct
    /// </summary>
    /// <param name="field">Field name to sort by</param>
    /// <param name="ascending">Sort direction (true = ascending, false = descending)</param>
    /// <param name="fieldType">Optional field type for typed ordering</param>
    [SetsRequiredMembers]
    public OrderByClause(string field, bool ascending = true, MetadataV2FieldType? fieldType = null)
    {
        Field = field;
        Ascending = ascending;
        FieldType = fieldType;
    }

    /// <summary>
    /// Creates an ascending order by clause
    /// </summary>
    /// <param name="field">Field to sort by</param>
    /// <returns>Order by clause instance</returns>
    public static OrderByClause Asc(string field)
        => new() { Field = field, Ascending = true };

    /// <summary>
    /// Creates a descending order by clause
    /// </summary>
    /// <param name="field">Field to sort by</param>
    /// <returns>Order by clause instance</returns>
    public static OrderByClause Desc(string field)
        => new() { Field = field, Ascending = false };
}

/// <summary>
/// Spatial relationship types for filtering
/// </summary>
public enum SpatialRelationship
{
    /// <summary>
    /// Features that intersect the filter geometry
    /// </summary>
    Intersects,

    /// <summary>
    /// Features completely within the filter geometry
    /// </summary>
    Within,

    /// <summary>
    /// Features that contain the filter geometry
    /// </summary>
    Contains,

    /// <summary>
    /// Features whose envelope intersects the filter geometry
    /// </summary>
    EnvelopeIntersects,

    /// <summary>
    /// Features that cross the filter geometry (lines through polygons)
    /// </summary>
    Crosses,

    /// <summary>
    /// Features that touch but don't overlap the filter geometry (adjacent parcels)
    /// </summary>
    Touches,

    /// <summary>
    /// Features that partially overlap the filter geometry
    /// </summary>
    Overlaps,

    /// <summary>
    /// Features that don't touch the filter geometry at all
    /// </summary>
    Disjoint,

    /// <summary>
    /// Features that are geometrically identical to the filter geometry
    /// </summary>
    Equals,

    /// <summary>
    /// Features within a specified distance of the filter geometry (ST_DWithin)
    /// </summary>
    WithinDistance,

    /// <summary>
    /// Features beyond a specified distance from the filter geometry
    /// </summary>
    BeyondDistance,

    /// <summary>
    /// K-Nearest Neighbor query - returns K closest features to the filter geometry
    /// </summary>
    NearestNeighbor
}

/// <summary>
/// Units for distance measurements in spatial queries
/// </summary>
public enum DistanceUnit
{
    /// <summary>
    /// Distance in meters (default for geography types)
    /// </summary>
    Meters,

    /// <summary>
    /// Distance in feet
    /// </summary>
    Feet,

    /// <summary>
    /// Distance in kilometers
    /// </summary>
    Kilometers,

    /// <summary>
    /// Distance in miles
    /// </summary>
    Miles
}
