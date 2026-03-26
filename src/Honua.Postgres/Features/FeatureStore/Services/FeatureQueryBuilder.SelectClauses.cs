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

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
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

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
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

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
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

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
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
}
