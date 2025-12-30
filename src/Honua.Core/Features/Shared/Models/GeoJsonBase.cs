// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Base structure for GeoJSON features across protocols
/// </summary>
public readonly record struct GeoJsonFeatureBase
{
    /// <summary>
    /// Feature identifier (typically ObjectId)
    /// </summary>
    public object? Id { get; init; }

    /// <summary>
    /// Feature properties/attributes
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }

    /// <summary>
    /// Indicates if this feature has geometry data
    /// </summary>
    public required bool HasGeometry { get; init; }

    /// <summary>
    /// Creates a GeoJSON feature base without geometry
    /// </summary>
    /// <param name="id">Feature identifier</param>
    /// <param name="properties">Feature properties</param>
    /// <returns>GeoJSON feature base instance</returns>
    public static GeoJsonFeatureBase Create(object? id, IReadOnlyDictionary<string, object?> properties)
        => new()
        {
            Id = id,
            Properties = properties,
            HasGeometry = false
        };

    /// <summary>
    /// Creates a GeoJSON feature base with geometry
    /// </summary>
    /// <param name="id">Feature identifier</param>
    /// <param name="properties">Feature properties</param>
    /// <param name="hasGeometry">Whether feature has geometry</param>
    /// <returns>GeoJSON feature base instance</returns>
    public static GeoJsonFeatureBase Create(object? id, IReadOnlyDictionary<string, object?> properties, bool hasGeometry)
        => new()
        {
            Id = id,
            Properties = properties,
            HasGeometry = hasGeometry
        };
}

/// <summary>
/// Base structure for GeoJSON geometry across protocols
/// </summary>
public readonly record struct GeoJsonGeometryBase
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.)
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Indicates if this geometry has coordinate data
    /// </summary>
    public required bool HasCoordinates { get; init; }

    /// <summary>
    /// Indicates if this is a geometry collection
    /// </summary>
    public bool IsGeometryCollection => Type.Equals("GeometryCollection", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a geometry base for simple geometries
    /// </summary>
    /// <param name="type">Geometry type</param>
    /// <returns>GeoJSON geometry base instance</returns>
    public static GeoJsonGeometryBase Create(string type)
        => new()
        {
            Type = type,
            HasCoordinates = !type.Equals("GeometryCollection", StringComparison.OrdinalIgnoreCase)
        };

    /// <summary>
    /// Creates a geometry base with explicit coordinate flag
    /// </summary>
    /// <param name="type">Geometry type</param>
    /// <param name="hasCoordinates">Whether geometry has coordinates</param>
    /// <returns>GeoJSON geometry base instance</returns>
    public static GeoJsonGeometryBase Create(string type, bool hasCoordinates)
        => new()
        {
            Type = type,
            HasCoordinates = hasCoordinates
        };
}

/// <summary>
/// Base structure for paged query responses
/// </summary>
public readonly record struct PagedResponseBase
{
    /// <summary>
    /// Total number of items available (may exceed items returned)
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// Number of items in this response
    /// </summary>
    public required int ReturnedCount { get; init; }

    /// <summary>
    /// Indicates if the transfer limit was exceeded
    /// </summary>
    public bool ExceededTransferLimit { get; init; }

    /// <summary>
    /// Creates a paged response base
    /// </summary>
    /// <param name="returnedCount">Number of items returned</param>
    /// <returns>Paged response base instance</returns>
    public static PagedResponseBase Create(int returnedCount)
        => new() { ReturnedCount = returnedCount };

    /// <summary>
    /// Creates a paged response base with total count
    /// </summary>
    /// <param name="returnedCount">Number of items returned</param>
    /// <param name="totalCount">Total count available</param>
    /// <returns>Paged response base instance</returns>
    public static PagedResponseBase Create(int returnedCount, long? totalCount)
        => new() { ReturnedCount = returnedCount, TotalCount = totalCount };

    /// <summary>
    /// Creates a paged response base with all parameters
    /// </summary>
    /// <param name="returnedCount">Number of items returned</param>
    /// <param name="totalCount">Total count available</param>
    /// <param name="exceededTransferLimit">Whether transfer limit was exceeded</param>
    /// <returns>Paged response base instance</returns>
    public static PagedResponseBase Create(int returnedCount, long? totalCount, bool exceededTransferLimit)
        => new()
        {
            ReturnedCount = returnedCount,
            TotalCount = totalCount,
            ExceededTransferLimit = exceededTransferLimit
        };
}
