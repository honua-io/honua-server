// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public readonly record struct GeoJsonFeatureBase
{
    public object? Id { get; init; }

    public required IReadOnlyDictionary<string, object?> Properties { get; init; }

    public required bool HasGeometry { get; init; }

    public static GeoJsonFeatureBase Create(object? id, IReadOnlyDictionary<string, object?> properties)
        => new()
        {
            Id = id,
            Properties = properties,
            HasGeometry = false
        };

    public static GeoJsonFeatureBase Create(object? id, IReadOnlyDictionary<string, object?> properties, bool hasGeometry)
        => new()
        {
            Id = id,
            Properties = properties,
            HasGeometry = hasGeometry
        };
}

public readonly record struct GeoJsonGeometryBase
{
    public required string Type { get; init; }

    public required bool HasCoordinates { get; init; }

    public bool IsGeometryCollection => Type.Equals("GeometryCollection", StringComparison.OrdinalIgnoreCase);

    public static GeoJsonGeometryBase Create(string type)
        => new()
        {
            Type = type,
            HasCoordinates = !type.Equals("GeometryCollection", StringComparison.OrdinalIgnoreCase)
        };

    public static GeoJsonGeometryBase Create(string type, bool hasCoordinates)
        => new()
        {
            Type = type,
            HasCoordinates = hasCoordinates
        };
}

public readonly record struct PagedResponseBase
{
    public long? TotalCount { get; init; }

    public required int ReturnedCount { get; init; }

    public bool ExceededTransferLimit { get; init; }

    public static PagedResponseBase Create(int returnedCount)
        => new() { ReturnedCount = returnedCount };

    public static PagedResponseBase Create(int returnedCount, long? totalCount)
        => new() { ReturnedCount = returnedCount, TotalCount = totalCount };

    public static PagedResponseBase Create(int returnedCount, long? totalCount, bool exceededTransferLimit)
        => new()
        {
            ReturnedCount = returnedCount,
            TotalCount = totalCount,
            ExceededTransferLimit = exceededTransferLimit
        };
}
