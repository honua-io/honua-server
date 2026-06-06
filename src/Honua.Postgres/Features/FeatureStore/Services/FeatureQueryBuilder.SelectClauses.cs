// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    /// <summary>
    /// Whether the SELECT clause should project the runtime <c>distance</c> column.
    /// <c>returnDistance</c> is spec-compliant for the general layer /query operation:
    /// the column is computed whenever a spatial filter geometry is present (KNN or any
    /// other relationship), measuring each feature's geodesic distance from the query
    /// geometry. The boolean mirrors the parameter binding in
    /// <c>FeatureDataAccess.AddQueryParameters</c>, which prepends the distance geometry
    /// parameter ahead of the WHERE parameters.
    /// </summary>
    internal static bool ShouldComputeDistance(SpatialFilter? spatialFilter)
        => spatialFilter is { ReturnDistance: true };

    /// <summary>
    /// Builds the <c>ST_Distance(...)</c> geography operand for the runtime distance column
    /// and, for non-KNN queries, appends the filter geometry to the WHERE-parameter list so
    /// it is bound in positional order alongside the rest of the SELECT clause.
    /// </summary>
    /// <remarks>
    /// KNN queries intentionally do not append here: their distance geometry is bound
    /// up-front by <c>FeatureDataAccess.AddQueryParameters</c> (ahead of the WHERE params),
    /// which is the established KNN ordering. Non-KNN queries have no such manual binding, so
    /// the geometry is threaded through <paramref name="parameters"/> instead — this keeps the
    /// distance parameter present only when the SELECT clause actually projects the column.
    /// </remarks>
    private string BuildDistanceSelectOperand(
        SpatialFilter filter,
        FeatureQuery query,
        bool isKnnQuery,
        ref int paramIndex,
        List<object> parameters)
    {
        var expression = BuildGeographyFilterExpression(filter, query, ref paramIndex);
        if (!isKnnQuery)
        {
            parameters.Add(filter.Geometry);
        }

        return expression;
    }

    private void BuildSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            // The runtime distance column requires KNN/spatial parameter ordering the overlay does not yet
            // thread; versioned distance reads are unsupported in v1. DEFAULT is unaffected.
            GuardVersionedReadSupported(query, "select-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-gml-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGeoJsonSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-geojson-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildRawGeoJsonSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);
        var (publicIdSelect, attributesSelect) = BuildRawAttributeSelectExpressions(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-raw-geojson-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {publicIdSelect} AS public_id, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {publicIdSelect} AS public_id, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildKmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryKmlExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-kml-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private static string BuildAttributesSelectExpression(
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.ExcludeAttributes)
        {
            return "NULL";
        }

        if (!query.OutFields.HasValue || query.OutFields.Value.IsDefault)
        {
            return DatabaseSchema.AttributesColumn;
        }

        var requestedOutFields = query.OutFields.Value;
        if (requestedOutFields.IsEmpty)
        {
            return "NULL";
        }

        var projectedFields = new List<string>(requestedOutFields.Length);
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldName in requestedOutFields)
        {
            if (!IsValidFieldName(fieldName))
            {
                throw new ArgumentException($"Invalid field name for projection: {fieldName}");
            }

            if (!DatabaseSchema.CanUseJsonPath(fieldName) || !seenFields.Add(fieldName))
            {
                continue;
            }

            var fieldParamIndex = paramIndex++;
            parameters.Add(fieldName);
            var escapedFieldName = fieldName.Replace("'", "''", StringComparison.Ordinal);
            projectedFields.Add($"'{escapedFieldName}', {DatabaseSchema.AttributesColumn} -> ${fieldParamIndex}");
        }

        if (projectedFields.Count == 0)
        {
            return "NULL";
        }

        return $"jsonb_build_object({string.Join(", ", projectedFields)})::text";
    }

    private static (string PublicIdSelect, string AttributesSelect) BuildRawAttributeSelectExpressions(
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.ExcludeAttributes)
        {
            return ("NULL", "NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.PublicIdAttributeName) &&
            IsValidFieldName(query.PublicIdAttributeName))
        {
            var fieldParamIndex = paramIndex++;
            parameters.Add(query.PublicIdAttributeName);
            var fieldParam = $"${fieldParamIndex}";
            return (
                $"{DatabaseSchema.AttributesColumn} -> {fieldParam}",
                $"({DatabaseSchema.AttributesColumn} - {fieldParam})::text");
        }

        return ("NULL", $"{DatabaseSchema.AttributesColumn}::text");
    }
}
