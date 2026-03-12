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
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGeoJsonSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildKmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex)
    {
        var geometrySelect = _geometryProcessor.GetGeometryKmlExpression(geometryStorageType, query);

        if (isKnnQuery && spatialFilter!.Value.ReturnDistance)
        {
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {DatabaseSchema.AttributesColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }
}
