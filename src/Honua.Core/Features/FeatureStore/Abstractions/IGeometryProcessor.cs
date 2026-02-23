// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for geometry processing operations
/// </summary>
internal interface IGeometryProcessor
{
    /// <summary>
    /// Gets the SQL expression for selecting geometry with proper transformation
    /// </summary>
    string GetGeometrySelectExpression(GeometryStorageType storageType, FeatureQuery query);

    /// <summary>
    /// Gets the SQL expression for selecting GML geometry
    /// </summary>
    string GetGeometryGmlExpression(GeometryStorageType storageType, FeatureQuery query);

    /// <summary>
    /// Gets the SQL expression for writing/inserting geometry
    /// </summary>
    string GetGeometryWriteExpression(GeometryStorageType storageType, string parameterName, int? layerSrid);

    /// <summary>
    /// Gets the geometry operand for spatial operations
    /// </summary>
    string GetGeometryOperand(GeometryStorageType storageType, string? columnExpression = null, int? layerSrid = null);

    /// <summary>
    /// Builds spatial filter geometry expression for queries
    /// </summary>
    string BuildSpatialFilterGeometryExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex);

    /// <summary>
    /// Converts distance to meters based on unit
    /// </summary>
    double ConvertDistanceToMeters(double distance, DistanceUnit unit);

    /// <summary>
    /// Gets the geometry operand for geography (WGS84) operations such as KNN distance queries
    /// </summary>
    string GetGeographyOperand(GeometryStorageType storageType, int? layerSrid);
}
