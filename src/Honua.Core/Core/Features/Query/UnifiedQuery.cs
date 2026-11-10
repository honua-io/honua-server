// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Query;

public readonly record struct UnifiedQuery
{
    public QueryFilter? Filter { get; init; }

    public SpatialFilter? SpatialFilter { get; init; }

    public TemporalFilter? TemporalFilter { get; init; }

    public ImmutableArray<long>? ObjectIds { get; init; }

    public ImmutableArray<string>? OutFields { get; init; }

    public int? Offset { get; init; }

    public int? Limit { get; init; }

    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

    public QueryCrs? OutputCrs { get; init; }

    public QueryAggregation? Aggregation { get; init; }

    public QueryHints? Hints { get; init; }

    public ImmutableDictionary<string, object>? Extensions { get; init; }

    public static UnifiedQuery WithFilter(QueryFilter filter)
        => new() { Filter = filter };

    public static UnifiedQuery WithSpatialFilter(SpatialFilter spatialFilter)
        => new() { SpatialFilter = spatialFilter };

    public static UnifiedQuery WithPagination(int offset, int limit)
        => new() { Offset = offset, Limit = limit };

    public static UnifiedQuery WithObjectIds(ImmutableArray<long> objectIds)
        => new() { ObjectIds = objectIds };

    public bool IsEmpty => Filter == null &&
                          SpatialFilter == null &&
                          TemporalFilter == null &&
                          ObjectIds?.IsDefaultOrEmpty != false;

    public bool HasFilters => Filter != null ||
                             SpatialFilter != null ||
                             TemporalFilter != null ||
                             ObjectIds?.IsDefaultOrEmpty == false;

    public bool RequiresAggregation => Aggregation != null &&
                                      (Aggregation.Value.Statistics?.IsDefaultOrEmpty == false ||
                                       Aggregation.Value.GroupByFields?.IsDefaultOrEmpty == false);

    public int GetEffectiveLimit(int defaultLimit) => Limit ?? defaultLimit;

    public int GetEffectiveOffset() => Offset ?? 0;
}

public readonly record struct QueryFilter
{
    public FilterExpression? Expression { get; init; }

    public SqlFragment? SqlFragment { get; init; }

    public FilterSource? Source { get; init; }

    public static QueryFilter FromExpression(FilterExpression expression, FilterSource? source = null)
        => new() { Expression = expression, Source = source };

    public static QueryFilter FromSql(SqlFragment sqlFragment, FilterSource? source = null)
        => new() { SqlFragment = sqlFragment, Source = source };

    public FilterExpression? GetFilterExpression() => Expression;

    public SqlFragment? GetSqlFragment() => SqlFragment;
}

public readonly record struct FilterSource
{
    public string Text { get; init; }

    public FilterLanguage Language { get; init; }

    public string Protocol { get; init; }

    [SetsRequiredMembers]
    public FilterSource(string text, FilterLanguage language, string protocol)
    {
        Text = text;
        Language = language;
        Protocol = protocol;
    }
}

public readonly record struct QueryCrs
{
    public int? Srid { get; init; }

    public AxisOrder? AxisOrder { get; init; }

    public string? Uri { get; init; }

    public static QueryCrs Create(int srid, AxisOrder? axisOrder = null, string? uri = null)
        => new() { Srid = srid, AxisOrder = axisOrder, Uri = uri };
}

public readonly record struct QueryAggregation
{
    public ImmutableArray<StatisticDefinition>? Statistics { get; init; }

    public ImmutableArray<string>? GroupByFields { get; init; }

    public QueryFilter? HavingFilter { get; init; }

    public bool Distinct { get; init; }

    public static QueryAggregation Create(
        ImmutableArray<StatisticDefinition>? statistics = null,
        ImmutableArray<string>? groupByFields = null,
        bool distinct = false)
        => new() { Statistics = statistics, GroupByFields = groupByFields, Distinct = distinct };
}

public readonly record struct QueryHints
{
    public bool PreferStreaming { get; init; }

    public bool EnableCaching { get; init; }

    public long? EstimatedResultSize { get; init; }

    public bool RequireExactCount { get; init; }

    public int? TimeoutMs { get; init; }

    public ImmutableDictionary<string, object>? OptimizationHints { get; init; }

    public static QueryHints Create(
        bool preferStreaming = false,
        bool enableCaching = true,
        bool requireExactCount = true)
        => new() { PreferStreaming = preferStreaming, EnableCaching = enableCaching, RequireExactCount = requireExactCount };
}
