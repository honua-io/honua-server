// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Cross-layer spatial join SQL — split out from <c>FeatureQueryBuilder.Analytics.cs</c>
/// because the join shape needs a target CTE plus an aliased outer JOIN, which is
/// noticeably different from the single-source clustering / buffer / density builders.
/// </summary>
internal sealed partial class FeatureQueryBuilder
{
    /// <summary>
    /// Builds a spatial join query that filters the target layer with the shared
    /// <see cref="FeatureQuery"/> AST and joins each target row to matching join-layer
    /// rows using the requested spatial predicate. Each output row carries the target
    /// feature plus a match count, optional aggregated carry fields, and optional
    /// aggregate statistics computed across matching join rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target layer is materialised in a <c>WITH target_src AS (...)</c> CTE so the
    /// existing <c>AppendWhereClause</c> / <c>AppendTemporalFilter</c> / <c>AppendSpatialFilter</c>
    /// helpers can keep emitting unqualified column references. The CTE applies
    /// <c>LIMIT (maxInputFeatures + 1)</c> so the handler can detect overflow without
    /// scanning the full target layer.
    /// </para>
    /// <para>
    /// The join uses <c>LEFT JOIN</c> so target rows with zero matches still appear in
    /// the result with <c>matchCount = 0</c>. Carry-field arrays are filtered to
    /// non-matching rows so the empty case yields an empty array rather than
    /// <c>{NULL}</c>.
    /// </para>
    /// <para>
    /// Both layers are assumed to live in the same SRID. The handler is responsible
    /// for validating cross-layer compatibility before calling the builder; PostGIS
    /// will surface a clear error if the SRIDs do not match.
    /// </para>
    /// </remarks>
    public CoreParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();

            // Decode the target geometry once inside the CTE so the outer SELECT and the
            // join predicate reference a typed geometry column. In bytea storage mode
            // the raw column is `bytea`, so `t.geometry` flowing into `&&` / `ST_Intersects`
            // / `ST_AsGeoJSON` would fail at execution. Carrying the typed operand
            // forward keeps the join path correct across Geometry / Geography / Bytea.
            // The builder assumes both layers share the same SRID (see the XML remarks).
            var targetGeometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, targetQuery.SpatialReferenceSrid);

            // Target CTE — filtered subset of target layer with overflow guard.
            sql.Append("WITH target_src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.AttributesColumn}, ");
            sql.Append(CultureInfo.InvariantCulture, $"{targetGeometryOperand} AS geom");
            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName}");
            sql.Append(CultureInfo.InvariantCulture, $" WHERE {DatabaseSchema.LayerIdColumn} = $1");
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL");

            AppendWhereClause(sql, targetQuery, ref paramIndex, parameters);
            AppendTemporalFilter(sql, targetQuery, ref paramIndex, parameters);
            AppendSpatialFilter(sql, targetQuery, geometryStorageType, ref paramIndex, parameters);

            var maxInputParam = $"${paramIndex++}";
            parameters.Add(joinQuery.MaxInputFeatures + 1);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {maxInputParam})");

            // Bind the join layer id once so the spatial-index lookup is parameterised.
            var joinLayerParam = $"${paramIndex++}";
            parameters.Add(joinQuery.JoinLayerId);

            // Join-side operand — decoded the same way as the target so bytea storage
            // joins produce a valid geometry expression for the PostGIS operators.
            // Under Geometry storage this reduces to `j.geometry`, which keeps the
            // existing GIST-index friendly `&&` fast-path intact.
            var joinGeometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType,
                $"j.{DatabaseSchema.GeometryColumn}",
                targetQuery.SpatialReferenceSrid);

            // Predicate clause references the typed operands established above.
            var predicate = BuildSpatialJoinPredicate(
                joinQuery, "t.geom", joinGeometryOperand, ref paramIndex, parameters);

            // Outer SELECT — one row per target with stats over matching join rows.
            sql.Append(" SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"t.{DatabaseSchema.ObjectIdColumn} AS \"objectId\"");
            sql.Append(CultureInfo.InvariantCulture, $", t.{DatabaseSchema.AttributesColumn} AS \"attributes\"");
            sql.Append(", ST_AsGeoJSON(t.geom) AS \"geometry\"");
            sql.Append(CultureInfo.InvariantCulture, $", COUNT(j.{DatabaseSchema.ObjectIdColumn})::bigint AS \"matchCount\"");

            AppendJoinCarryFieldColumns(sql, joinQuery.CarryFields);
            AppendJoinStatisticsColumns(sql, joinQuery.OutStatistics);

            sql.Append(" FROM target_src t");
            sql.Append(CultureInfo.InvariantCulture,
                $" LEFT JOIN {_tableName} j ON j.{DatabaseSchema.LayerIdColumn} = {joinLayerParam}");
            sql.Append(CultureInfo.InvariantCulture,
                $" AND j.{DatabaseSchema.GeometryColumn} IS NOT NULL");
            sql.Append(CultureInfo.InvariantCulture, $" AND {predicate}");

            sql.Append(CultureInfo.InvariantCulture,
                $" GROUP BY t.{DatabaseSchema.ObjectIdColumn}, t.{DatabaseSchema.AttributesColumn}, t.geom");
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY t.{DatabaseSchema.ObjectIdColumn}");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Builds the SQL predicate that connects the target geometry expression to the
    /// join geometry expression. For <see cref="SpatialJoinPredicate.DWithin"/> the
    /// distance is pushed through as a positional parameter and applied against the
    /// geography projection so the threshold is interpreted in meters regardless of
    /// layer SRID. Callers are responsible for supplying storage-aware expressions
    /// (see <see cref="GeometryProcessor.GetGeometryOperand"/>) so the predicate
    /// works across Geometry, Geography and Bytea storage modes.
    /// </summary>
    private static string BuildSpatialJoinPredicate(
        SpatialJoinQuery joinQuery,
        string targetGeometryExpression,
        string joinGeometryExpression,
        ref int paramIndex,
        List<object> parameters)
    {
        switch (joinQuery.Predicate)
        {
            case SpatialJoinPredicate.Intersects:
                return FormattableString.Invariant(
                    $"{targetGeometryExpression} && {joinGeometryExpression} AND ST_Intersects({targetGeometryExpression}, {joinGeometryExpression})");

            case SpatialJoinPredicate.Contains:
                return FormattableString.Invariant(
                    $"{targetGeometryExpression} && {joinGeometryExpression} AND ST_Contains({targetGeometryExpression}, {joinGeometryExpression})");

            case SpatialJoinPredicate.Within:
                return FormattableString.Invariant(
                    $"{targetGeometryExpression} && {joinGeometryExpression} AND ST_Within({targetGeometryExpression}, {joinGeometryExpression})");

            case SpatialJoinPredicate.DWithin:
                if (!joinQuery.DistanceMeters.HasValue || joinQuery.DistanceMeters.Value < 0)
                {
                    throw new ArgumentException(
                        "DWithin spatial join predicate requires a non-negative DistanceMeters value.",
                        nameof(joinQuery));
                }

                var distanceParam = $"${paramIndex++}";
                parameters.Add(joinQuery.DistanceMeters.Value);
                return FormattableString.Invariant(
                    $"ST_DWithin({targetGeometryExpression}::geography, {joinGeometryExpression}::geography, {distanceParam})");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(joinQuery), joinQuery.Predicate, "Unsupported spatial join predicate.");
        }
    }

    /// <summary>
    /// Appends carry-field columns aggregated as <c>array_agg</c> over the matching
    /// join rows. Field names are validated against the same regex as the rest of
    /// the builder before they are emitted as JSON path expressions qualified with
    /// the <c>j</c> alias.
    /// </summary>
    private static void AppendJoinCarryFieldColumns(
        StringBuilder sql, ImmutableArray<string>? carryFields)
    {
        if (!carryFields.HasValue || carryFields.Value.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var field in carryFields.Value)
        {
            if (!IsValidFieldName(field))
            {
                throw new ArgumentException($"Invalid carry field name: {field}");
            }

            var fieldExpr = GetJoinFieldExpression(field);

            // FILTER (WHERE j.objectid IS NOT NULL) suppresses the {NULL} that LEFT JOIN
            // would otherwise produce for target rows with zero matches.
            // COALESCE to '{}' returns an explicit empty array in that case.
            sql.Append(CultureInfo.InvariantCulture,
                $", COALESCE(array_agg({fieldExpr}) FILTER (WHERE j.{DatabaseSchema.ObjectIdColumn} IS NOT NULL), '{{}}') AS {SanitizeAlias(field)}");
        }
    }

    /// <summary>
    /// Appends aggregate statistic columns computed over the join-side rows. Field
    /// names are validated and reused via <see cref="BuildAggregateExpression"/>
    /// for parity with <c>queryStatistics</c> / <c>queryH3</c>.
    /// </summary>
    private static void AppendJoinStatisticsColumns(
        StringBuilder sql, ImmutableArray<StatisticDefinition>? outStatistics)
    {
        if (!outStatistics.HasValue || outStatistics.Value.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var stat in outStatistics.Value)
        {
            if (!IsValidFieldName(stat.OnStatisticField))
            {
                throw new ArgumentException($"Invalid statistic field name: {stat.OnStatisticField}");
            }

            if (!IsValidFieldName(stat.OutStatisticFieldName))
            {
                throw new ArgumentException($"Invalid output statistic field name: {stat.OutStatisticFieldName}");
            }

            var statFieldExpr = GetJoinFieldExpression(stat.OnStatisticField);
            var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr);
            sql.Append(CultureInfo.InvariantCulture,
                $", {aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
        }
    }

    /// <summary>
    /// Returns the SQL expression that resolves <paramref name="fieldName"/> against
    /// the join-side alias <c>j</c>. <c>objectid</c> is treated as a direct column;
    /// every other field name is rendered as a JSONB text extraction against
    /// <c>j.attributes</c> using <see cref="DatabaseSchema.BuildJsonPath"/> so single
    /// quotes are escaped consistently with the rest of the builder.
    /// </summary>
    private static string GetJoinFieldExpression(string fieldName)
    {
        if (string.Equals(fieldName, DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase))
        {
            return $"j.{DatabaseSchema.ObjectIdColumn}";
        }

        return DatabaseSchema.BuildJsonPath(fieldName, $"j.{DatabaseSchema.AttributesColumn}");
    }
}
