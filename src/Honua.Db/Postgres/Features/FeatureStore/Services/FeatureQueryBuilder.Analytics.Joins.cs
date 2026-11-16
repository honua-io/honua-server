// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    public CoreParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
        => BuildSpatialJoinQuery(targetLayerId, targetQuery, joinQuery, joinLayerEnforcedFilter: null, geometryStorageType);

    public CoreParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        SqlFragment? joinLayerEnforcedFilter,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(targetQuery, "spatial-join");
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
            var targetGeometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType, DatabaseSchema.GeometryColumn, targetQuery.SpatialReferenceSrid);

            // Target CTE — filtered subset of target layer with overflow guard.
            // The target layer's field masks are subtracted where the attributes
            // enter the CTE, so the "attributes" output never carries a masked value.
            // Without masks this is the bare column.
            var targetAttributesSource = BuildMaskedAttributesColumn(
                ResolveMaskedFields(targetQuery), ref paramIndex, parameters);

            sql.Append("WITH target_src AS (SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"{DatabaseSchema.ObjectIdColumn}, ");
            sql.Append(targetAttributesSource == DatabaseSchema.AttributesColumn
                ? $"{DatabaseSchema.AttributesColumn}, "
                : $"{targetAttributesSource} AS {DatabaseSchema.AttributesColumn}, ");
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

            // The join layer's permanent filter (when present) is applied inside a
            // derived table below so its unqualified column references
            // (attributes->>'...') resolve against the join side instead of
            // colliding with target_src columns in the outer join scope.
            string? joinEnforcedSql = null;
            if (joinLayerEnforcedFilter != null)
            {
                joinEnforcedSql = ConvertNamedParametersToPositional(joinLayerEnforcedFilter.Sql, ref paramIndex);
                foreach (var param in joinLayerEnforcedFilter.Parameters)
                {
                    parameters.Add(param ?? DBNull.Value);
                }
            }

            // Join-side operand — decoded with the join layer's own SRID so bytea
            // storage joins produce a valid geometry expression for the PostGIS
            // operators. Under Geometry storage this reduces to `j.geometry`,
            // which keeps the existing GIST-index friendly `&&` fast-path intact
            // when the join layer happens to share the target layer's SRID.
            var joinGeometryOperand = _geometryProcessor.GetGeometryOperand(
                geometryStorageType,
                $"j.{DatabaseSchema.GeometryColumn}",
                joinQuery.JoinLayerSrid);

            // Cross-CRS transform: when both SRIDs are known and differ, wrap the
            // join operand with ST_Transform so the spatial predicate evaluates in
            // the target layer's CRS. Same-CRS or unknown SRIDs skip the wrap so
            // PostGIS can keep using the GiST index on the join layer.
            if (targetQuery.SpatialReferenceSrid.HasValue &&
                joinQuery.JoinLayerSrid.HasValue &&
                targetQuery.SpatialReferenceSrid.Value != joinQuery.JoinLayerSrid.Value)
            {
                joinGeometryOperand = FormattableString.Invariant(
                    $"ST_Transform({joinGeometryOperand}, {targetQuery.SpatialReferenceSrid.Value})");
            }

            // Predicate clause references the typed operands established above.
            var predicate = BuildSpatialJoinPredicate(
                joinQuery, "t.geom", joinGeometryOperand, ref paramIndex, parameters);

            // Outer SELECT — one row per target with stats over matching join rows.
            // The target geometry is reprojected to EPSG:4326 before serialisation
            // so the GeoJSON response matches the application/geo+json WGS 84
            // contract regardless of the target layer's stored SRID.
            sql.Append(" SELECT ");
            sql.Append(CultureInfo.InvariantCulture, $"t.{DatabaseSchema.ObjectIdColumn} AS \"objectId\"");
            sql.Append(CultureInfo.InvariantCulture, $", t.{DatabaseSchema.AttributesColumn} AS \"attributes\"");
            sql.Append(", ST_AsGeoJSON(ST_Transform(t.geom, 4326)) AS \"geometry\"");
            sql.Append(CultureInfo.InvariantCulture, $", COUNT(j.{DatabaseSchema.ObjectIdColumn})::bigint AS \"matchCount\"");

            AppendJoinCarryFieldColumns(sql, joinQuery.CarryFields);
            AppendJoinStatisticsColumns(sql, joinQuery.OutStatistics);

            sql.Append(" FROM target_src t");
            if (joinEnforcedSql != null)
            {
                // Derived-table form: layer/geometry/permanent-filter predicates move
                // into the subquery (equivalent for LEFT JOIN) so the enforced filter's
                // unqualified column references bind to the join layer's rows.
                sql.Append(CultureInfo.InvariantCulture,
                    $" LEFT JOIN (SELECT * FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = {joinLayerParam}");
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND {DatabaseSchema.GeometryColumn} IS NOT NULL AND ({joinEnforcedSql})) j");
                sql.Append(CultureInfo.InvariantCulture, $" ON {predicate}");
            }
            else
            {
                sql.Append(CultureInfo.InvariantCulture,
                    $" LEFT JOIN {_tableName} j ON j.{DatabaseSchema.LayerIdColumn} = {joinLayerParam}");
                sql.Append(CultureInfo.InvariantCulture,
                    $" AND j.{DatabaseSchema.GeometryColumn} IS NOT NULL");
                sql.Append(CultureInfo.InvariantCulture, $" AND {predicate}");
            }

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

            // Operand order is fixed by the canonical enum member names
            // — the join geometry is the subject of
            // JoinContainsTarget, the target geometry the subject of
            // TargetContainsJoin. The `&&` bounding-box pre-filter is symmetric,
            // so it stays in target-then-join order for both.
            case SpatialJoinPredicate.JoinContainsTarget:
                return FormattableString.Invariant(
                    $"{targetGeometryExpression} && {joinGeometryExpression} AND ST_Contains({joinGeometryExpression}, {targetGeometryExpression})");

            case SpatialJoinPredicate.TargetContainsJoin:
                return FormattableString.Invariant(
                    $"{targetGeometryExpression} && {joinGeometryExpression} AND ST_Contains({targetGeometryExpression}, {joinGeometryExpression})");

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
            var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr, stat.FieldType);
            sql.Append(CultureInfo.InvariantCulture,
                $", {aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
        }
    }

    private static string GetJoinFieldExpression(string fieldName)
    {
        if (string.Equals(fieldName, DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase))
        {
            return $"j.{DatabaseSchema.ObjectIdColumn}";
        }

        return DatabaseSchema.BuildJsonPath(fieldName, $"j.{DatabaseSchema.AttributesColumn}");
    }
}
