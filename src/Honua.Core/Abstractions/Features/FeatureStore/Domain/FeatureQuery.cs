// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct FeatureQuery
{
    public string? Where { get; init; }

    public SqlFragment? SqlFilter { get; init; }

    public const string SqlFilterSupplementsWhereExtensionKey = "sqlFilterSupplementsWhere";

    // A provider that cannot translate the extra parameterized predicates must fail closed instead of dropping them.
    public bool SqlFilterSupplementsWhere { get; init; }

    public FeatureTextSearch? TextSearch { get; init; }

    public SqlFragment? EnforcedSqlFilter { get; init; }

    // Listed fields are dropped for every protocol so a restricted role never receives a masked value.
    public ImmutableArray<string>? EnforcedMaskedFields { get; init; }

    public ImmutableArray<long>? ObjectIds { get; init; }

    public ImmutableArray<string>? OutFields { get; init; }

    public bool ExcludeAttributes { get; init; }

    public SpatialFilter? SpatialFilter { get; init; }

    public MetadataV2GeometryType? GeometryType { get; init; }

    public string? PublicIdAttributeName { get; init; }

    public int? SpatialReferenceSrid { get; init; }

    public int? OutputSrid { get; init; }

    public DatumTransformationSelection? OutputDatumTransformation { get; init; }

    public DatumTransformationSelection? SpatialFilterDatumTransformation { get; init; }

    public AxisOrder? OutputAxisOrder { get; init; }

    public RasterPointGrid? RasterPointGrid { get; init; }

    public TemporalFilter? TemporalFilter { get; init; }

    public bool IncludeNullGeometry { get; init; }

    // This flag only decides whether storage keeps Z in the canonical WKB; output adapters still choose whether to emit it.
    public bool IncludeZ { get; init; }

    public bool IncludeM { get; init; }

    public int? Offset { get; init; }

    public int? ResultOffset
    {
        readonly get => Offset;
        init => Offset = value;
    }

    public int? Limit { get; init; }

    public int? ResultRecordCount
    {
        readonly get => Limit;
        init => Limit = value;
    }

    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

    public bool Distinct { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public ImmutableArray<string>? GroupByFields { get; init; }

    public ImmutableArray<HavingCondition>? Having { get; init; }

    public TopFilter? TopFilter { get; init; }

    // A null version must run the non-versioned base query.
    public VersionContext? VersionContext { get; init; }

    public static FeatureQuery WithWhere(string where)
        => new() { Where = where };

    public static FeatureQuery WithSqlFilter(SqlFragment sqlFilter)
        => new() { SqlFilter = sqlFilter };

    public static FeatureQuery WithPaging(int offset, int limit)
        => new() { Offset = offset, Limit = limit };

    public static FeatureQuery WithSpatialFilter(SpatialFilter spatialFilter)
        => new() { SpatialFilter = spatialFilter };
}

public readonly record struct TemporalFilter
{
    public required string PropertyName { get; init; }

    public required TemporalPropertyType PropertyType { get; init; }

    public string? EndPropertyName { get; init; }

    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }
}

public enum TemporalPropertyType
{
    DateTime,

    Date
}

public readonly record struct SpatialFilter
{
    public required byte[] Geometry { get; init; }

    public int? Srid { get; init; }

    public required SpatialRelationship SpatialRelationship { get; init; }

    public double? Distance { get; init; }

    public DistanceUnit DistanceUnit { get; init; }

    public bool IsSimpleEnvelope { get; init; }

    public bool AllowEnvelopeOnly { get; init; }

    public double? EnvelopeMinX { get; init; }

    public double? EnvelopeMinY { get; init; }

    public double? EnvelopeMaxX { get; init; }

    public double? EnvelopeMaxY { get; init; }

    public int? NearestCount { get; init; }

    public bool ReturnDistance { get; init; }

    // Each antimeridian half is tested on its own because the union box would cover every longitude.
    public bool AntimeridianSplit { get; init; }

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

public enum NullOrdering
{
    Default = 0,

    NullsFirst,

    NullsLast
}

public readonly record struct OrderByClause
{
    public required string Field { get; init; }

    public bool Ascending { get; init; } = true;

    public MetadataV2FieldType? FieldType { get; init; }

    public NullOrdering NullOrdering { get; init; }

    [SetsRequiredMembers]
    public OrderByClause(string field, bool ascending = true, MetadataV2FieldType? fieldType = null)
    {
        Field = field;
        Ascending = ascending;
        FieldType = fieldType;
    }

    public static OrderByClause Asc(string field)
        => new() { Field = field, Ascending = true };

    public static OrderByClause Desc(string field)
        => new() { Field = field, Ascending = false };
}

public enum SpatialRelationship
{
    Intersects,

    // Within is the filter inside the feature, and the operands stay in that order.
    Within,

    // Contains is the filter around the feature, and the operands stay in that order.
    Contains,

    EnvelopeIntersects,

    Crosses,

    Touches,

    Overlaps,

    Disjoint,

    Equals,

    WithinDistance,

    BeyondDistance,

    NearestNeighbor
}

public enum DistanceUnit
{
    Meters,

    Feet,

    Kilometers,

    Miles
}
