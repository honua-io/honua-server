// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Processes geometry operations and SQL expression generation for PostgreSQL PostGIS
/// </summary>
internal sealed class GeometryProcessor : IGeometryProcessor
{
    public string GetGeometrySelectExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        return $"ST_AsBinary({baseGeometry}) AS {FeatureQueryEncoding.GeometryColumn}";
    }

    public string GetGeometryGmlExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        return $"ST_AsGML(3, {baseGeometry}, {FeatureQueryEncoding.GeometryTextPrecision}, 1)";
    }

    public string GetGeometryGeoJsonExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        return $"ST_AsGeoJSON({baseGeometry}, {FeatureQueryEncoding.GeometryTextPrecision}, 0)";
    }

    public string GetGeometryKmlExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        return $"ST_AsKML({baseGeometry}, {FeatureQueryEncoding.GeometryTextPrecision})";
    }

    public string GetGeometryWriteExpression(CoreGeometryStorageType storageType, string parameterName, int? layerSrid)
    {
        return storageType switch
        {
            CoreGeometryStorageType.Geometry => BuildGeometryWriteExpression(parameterName, layerSrid),
            CoreGeometryStorageType.Geography => BuildGeographyWriteExpression(parameterName, layerSrid),
            CoreGeometryStorageType.Bytea => parameterName,
            _ => parameterName
        };
    }

    public string GetGeometryOperand(CoreGeometryStorageType storageType, string? columnExpression = null, int? layerSrid = null)
    {
        var column = columnExpression ?? FeatureQueryEncoding.GeometryColumn;
        var operand = storageType switch
        {
            CoreGeometryStorageType.Geometry => column,
            CoreGeometryStorageType.Geography => $"{column}::geometry",
            CoreGeometryStorageType.Bytea => $"ST_GeomFromEWKB({column})",
            _ => column
        };

        if (storageType == CoreGeometryStorageType.Bytea && layerSrid.HasValue)
        {
            operand = $"ST_SetSRID({operand}, {layerSrid.Value})";
        }

        return operand;
    }

    public string BuildSpatialFilterGeometryExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex)
    {
        var parameterIndex = paramIndex++;
        var baseGeometry = $"ST_GeomFromEWKB(${parameterIndex})";
        var geometryExpression = baseGeometry;

        if (filter.Srid.HasValue)
        {
            geometryExpression =
                $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {filter.Srid.Value}))";
        }

        if (filter.Srid.HasValue && query.SpatialReferenceSrid.HasValue &&
            filter.Srid.Value != query.SpatialReferenceSrid.Value)
        {
            geometryExpression = $"ST_Transform({geometryExpression}, {query.SpatialReferenceSrid.Value})";
        }

        return geometryExpression;
    }

    public double ConvertDistanceToMeters(double distance, DistanceUnit unit)
    {
        return unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Feet => distance * 0.3048,
            DistanceUnit.Kilometers => distance * 1000,
            DistanceUnit.Miles => distance * 1609.344,
            _ => distance
        };
    }

    /// <summary>
    /// Gets the geometry operand for geography operations (WGS84)
    /// </summary>
    public string GetGeographyOperand(CoreGeometryStorageType storageType, int? layerSrid)
    {
        var geometryOperand = storageType switch
        {
            CoreGeometryStorageType.Geography => FeatureQueryEncoding.GeometryColumn,
            CoreGeometryStorageType.Geometry => FeatureQueryEncoding.GeometryColumn,
            CoreGeometryStorageType.Bytea => $"ST_GeomFromEWKB({FeatureQueryEncoding.GeometryColumn})",
            _ => FeatureQueryEncoding.GeometryColumn
        };

        if (storageType == CoreGeometryStorageType.Geography)
        {
            return geometryOperand;
        }

        if (storageType == CoreGeometryStorageType.Bytea && layerSrid.HasValue)
        {
            geometryOperand = $"ST_SetSRID({geometryOperand}, {layerSrid.Value})";
        }

        var wgs84Srid = SpatialReference.WGS84.Wkid;
        if (layerSrid.HasValue && layerSrid.Value != wgs84Srid)
        {
            geometryOperand = $"ST_Transform({geometryOperand}, {wgs84Srid})";
        }

        return $"{geometryOperand}::geography";
    }

    private static string BuildGeometryWriteExpression(string parameterName, int? layerSrid)
    {
        var baseGeometry = $"ST_GeomFromEWKB({parameterName})";
        if (!layerSrid.HasValue)
        {
            return baseGeometry;
        }

        return $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {layerSrid.Value}))";
    }

    private static string BuildGeographyWriteExpression(string parameterName, int? layerSrid)
    {
        var targetSrid = SpatialReference.WGS84.Wkid;
        var baseGeometry = $"ST_GeomFromEWKB({parameterName})";
        var assumedSrid = layerSrid ?? targetSrid;
        return $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {assumedSrid}))::geography";
    }
}
