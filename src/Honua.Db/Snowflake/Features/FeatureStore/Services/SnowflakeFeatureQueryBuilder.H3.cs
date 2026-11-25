// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Snowflake.Features.FeatureStore.Services;

internal sealed class SnowflakeH3QueryRequest
{
    public required SnowflakeLayerMapping Mapping { get; init; }
    public required FeatureQuery Query { get; init; }
    public required H3AggregationQuery H3Query { get; init; }
    public IReadOnlyList<string>? AttributeColumns { get; init; }
}

internal static partial class SnowflakeFeatureQueryBuilder
{
    private sealed class SnowflakeH3BuildState
    {
        public required SnowflakeQueryContext Context { get; init; }
        public required H3AggregationQuery H3Query { get; init; }
        public int PolyfillCteLength { get; set; }
    }

    public static ParameterizedQuery BuildH3AggregationQuery(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        H3AggregationQuery h3Query)
        => BuildH3AggregationQuery(new SnowflakeH3QueryRequest
        {
            Mapping = mapping,
            Query = query,
            H3Query = h3Query,
        });

    public static ParameterizedQuery BuildH3AggregationQuery(SnowflakeH3QueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateH3Query(request.Mapping, request.Query, request.H3Query);

        var context = CreateQueryContext(request.Mapping, request.AttributeColumns, request.Query);
        var state = new SnowflakeH3BuildState
        {
            Context = context,
            H3Query = request.H3Query,
            PolyfillCteLength = AppendH3PolyfillCte(context.Sql, request.H3Query, context.Parameters),
        };

        AppendH3BaseAggregation(state);
        ApplyH3KRingOrOrder(state);

        if (request.H3Query.MaxCells is > 0)
        {
            context.Sql.Append(" LIMIT ").Append(request.H3Query.MaxCells.Value.ToString(CultureInfo.InvariantCulture));
        }

        return new ParameterizedQuery(context.Sql.ToString(), context.Parameters);
    }

    private static void ValidateH3Query(
        SnowflakeLayerMapping mapping,
        FeatureQuery query,
        H3AggregationQuery h3Query)
    {
        h3Query.RejectSummaryDefinitions();

        ArgumentNullException.ThrowIfNull(mapping);
        GuardUnsupportedTemporalFilter(query);

        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            throw new InvalidOperationException(
                $"Layer {mapping.LayerId} has no geometry column configured; H3 aggregation is not available.");
        }

        if (h3Query.Resolution is < H3AggregationQuery.MinResolution or > H3AggregationQuery.MaxResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(h3Query),
                $"H3 resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}.");
        }

        if (h3Query.KRingDistance is < 0 or > H3AggregationQuery.MaxKRingDistance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(h3Query),
                $"H3 k-ring distance must be between 0 and {H3AggregationQuery.MaxKRingDistance}.");
        }

        if (mapping.GeometryColumnType == SnowflakeGeometryColumnType.Geometry
            && mapping.Srid is > 0 and not 4326)
        {
            throw new NotSupportedException(
                $"Snowflake H3 aggregation requires WGS 84 (EPSG:4326) geometry or GEOGRAPHY; layer {mapping.LayerId} uses SRID {mapping.Srid.Value}.");
        }
    }

    private static int AppendH3PolyfillCte(StringBuilder sb, H3AggregationQuery h3Query, List<object> parameters)
    {
        if (h3Query.PolyfillGeometry is not { Length: > 0 })
        {
            return 0;
        }

        if (h3Query.PolyfillSrid is > 0 and not 4326)
        {
            throw new NotSupportedException(
                $"Snowflake H3 polyfill requires WGS 84 (EPSG:4326) geometry; received SRID {h3Query.PolyfillSrid.Value}.");
        }

        parameters.Add(h3Query.PolyfillGeometry);
        parameters.Add(h3Query.Resolution);
        sb.Append("WITH polyfill_cells AS (SELECT VALUE::VARCHAR AS cell FROM TABLE(FLATTEN(INPUT => H3_POLYGON_TO_CELLS_STRINGS(TO_GEOGRAPHY(?, TRUE), ?))))");
        var polyfillCteLength = sb.Length;
        sb.Append(' ');
        return polyfillCteLength;
    }

    private static void AppendH3BaseAggregation(SnowflakeH3BuildState state)
    {
        var context = state.Context;
        var mapping = context.Mapping;
        var sb = context.Sql;
        var geomCol = mapping.QuotedGeometryColumn!;

        context.Parameters.Add(state.H3Query.Resolution);
        var cellExpr = mapping.GeometryColumnType == SnowflakeGeometryColumnType.Geography
            ? $"H3_POINT_TO_CELL_STRING(ST_CENTROID({geomCol}), ?)"
            : $"H3_LATLNG_TO_CELL_STRING(ST_Y(ST_CENTROID({geomCol})), ST_X(ST_CENTROID({geomCol})), ?)";

        sb.Append("SELECT _h3cell AS \"cellIndex\", ST_ASGEOJSON(H3_CELL_TO_BOUNDARY(_h3cell)) AS \"cellGeometry\"");
        if (state.H3Query.OutStatistics is { IsDefaultOrEmpty: false } stats)
        {
            sb.Append(", ").Append(BuildStatisticColumns(mapping, context.AttributeColumns, stats));
        }
        else
        {
            sb.Append(", COUNT(").Append(mapping.QuotedPrimaryKeyColumn).Append(") AS \"count\"");
        }

        sb.Append(" FROM (SELECT *, ").Append(cellExpr).Append(" AS _h3cell FROM ").Append(mapping.QuotedTableReference);
        sb.Append(" WHERE ").Append(geomCol).Append(" IS NOT NULL");
        AppendWhereClause(context);
        AppendObjectIdsFilter(sb, mapping, context.Query, context.Parameters);
        AppendSpatialFilter(sb, mapping, context.Query, context.Parameters);
        sb.Append(") _src");

        if (state.PolyfillCteLength > 0)
        {
            sb.Append(" WHERE _h3cell IN (SELECT cell FROM polyfill_cells)");
        }

        sb.Append(" GROUP BY _h3cell");
    }

    private static void ApplyH3KRingOrOrder(SnowflakeH3BuildState state)
    {
        var sb = state.Context.Sql;
        if (state.H3Query.KRingDistance is not > 0)
        {
            sb.Append(" ORDER BY COUNT(*) DESC");
            return;
        }

        var innerSql = sb.ToString();
        sb.Clear();
        if (state.PolyfillCteLength > 0)
        {
            sb.Append(innerSql[..state.PolyfillCteLength]);
            sb.Append(", aggregated AS (");
            sb.Append(innerSql[(state.PolyfillCteLength + 1)..]);
        }
        else
        {
            sb.Append("WITH aggregated AS (");
            sb.Append(innerSql);
        }

        state.Context.Parameters.Add(state.H3Query.KRingDistance.Value);
        sb.Append(") SELECT k.VALUE::VARCHAR AS \"cellIndex\", ST_ASGEOJSON(H3_CELL_TO_BOUNDARY(k.VALUE::VARCHAR)) AS \"cellGeometry\"");
        AppendH3KRingReaggregateColumns(sb, state.H3Query);
        sb.Append(" FROM aggregated a, LATERAL FLATTEN(INPUT => H3_GRID_DISK(a.\"cellIndex\", ?)) k GROUP BY k.VALUE::VARCHAR");
    }

    private static void AppendH3KRingReaggregateColumns(StringBuilder sb, H3AggregationQuery h3Query)
    {
        if (h3Query.OutStatistics is not { IsDefaultOrEmpty: false } stats)
        {
            sb.Append(", SUM(a.\"count\") AS \"count\"");
            return;
        }

        foreach (var stat in stats)
        {
            var alias = SnowflakeIdentifier.QuoteAttribute(stat.OutStatisticFieldName);
            var agg = stat.StatisticType switch
            {
                StatisticType.Count or StatisticType.Sum => $"SUM(a.{alias})",
                StatisticType.Min => $"MIN(a.{alias})",
                StatisticType.Max => $"MAX(a.{alias})",
                _ => $"AVG(a.{alias})",
            };
            sb.Append(", ").Append(agg).Append(" AS ").Append(alias);
        }
    }
}
