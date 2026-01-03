// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Processes geometry operations and SQL expression generation for PostgreSQL PostGIS
/// </summary>
internal sealed class GeometryProcessor : IGeometryProcessor
{
    private const string GeometryColumnName = "geometry";

    public string GetGeometrySelectExpression(GeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        if (storageType == GeometryStorageType.Bytea && !query.OutputSrid.HasValue)
        {
            return $"{GeometryColumnName} AS {GeometryColumnName}";
        }

        return $"ST_AsBinary({baseGeometry}) AS {GeometryColumnName}";
    }

    public string GetGeometryGmlExpression(GeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = $"ST_Transform({baseGeometry}, {query.OutputSrid.Value})";
        }

        return $"ST_AsGML(3, {baseGeometry}, 15, 1)";
    }

    public string GetGeometryWriteExpression(GeometryStorageType storageType, string parameterName, int? layerSrid)
    {
        return storageType switch
        {
            GeometryStorageType.Geometry => BuildGeometryWriteExpression(parameterName, layerSrid),
            GeometryStorageType.Geography => BuildGeographyWriteExpression(parameterName, layerSrid),
            GeometryStorageType.Bytea => parameterName,
            _ => parameterName
        };
    }

    public string GetGeometryOperand(GeometryStorageType storageType, string? columnExpression = null, int? layerSrid = null)
    {
        var column = columnExpression ?? GeometryColumnName;
        var operand = storageType switch
        {
            GeometryStorageType.Geometry => column,
            GeometryStorageType.Geography => $"{column}::geometry",
            GeometryStorageType.Bytea => $"ST_GeomFromEWKB({column})",
            _ => column
        };

        if (storageType == GeometryStorageType.Bytea && layerSrid.HasValue)
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
    public string GetGeographyOperand(GeometryStorageType storageType, int? layerSrid)
    {
        var geometryOperand = storageType switch
        {
            GeometryStorageType.Geography => GeometryColumnName,
            GeometryStorageType.Geometry => GeometryColumnName,
            GeometryStorageType.Bytea => $"ST_GeomFromEWKB({GeometryColumnName})",
            _ => GeometryColumnName
        };

        if (storageType == GeometryStorageType.Geography)
        {
            return geometryOperand;
        }

        if (storageType == GeometryStorageType.Bytea && layerSrid.HasValue)
        {
            geometryOperand = $"ST_SetSRID({geometryOperand}, {layerSrid.Value})";
        }

        if (layerSrid.HasValue && layerSrid.Value != 4326)
        {
            geometryOperand = $"ST_Transform({geometryOperand}, 4326)";
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

        return $"ST_Transform(ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {layerSrid.Value})), {layerSrid.Value})";
    }

    private static string BuildGeographyWriteExpression(string parameterName, int? layerSrid)
    {
        const int targetSrid = 4326;
        var baseGeometry = $"ST_GeomFromEWKB({parameterName})";
        var assumedSrid = layerSrid ?? targetSrid;
        return $"ST_Transform(ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {assumedSrid})), {targetSrid})::geography";
    }
}
