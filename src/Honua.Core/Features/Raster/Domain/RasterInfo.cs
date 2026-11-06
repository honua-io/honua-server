// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterInfo
{
    public required long Id { get; init; }

    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int BandCount { get; init; }

    public int? Srid { get; init; }

    public required string PixelType { get; init; }

    public double? NoDataValue { get; init; }

    public bool HasUniformNoDataValue { get; init; }

    public double[]? GeoTransform { get; init; }

    public RasterExtent? Extent { get; init; }

    public DateTimeOffset? AcquisitionDate { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public RasterSensorMetadata? SensorMetadata { get; init; }
}

public sealed record RasterSensorMetadata
{
    public required long RasterDataId { get; init; }

    public string? SensorName { get; init; }

    public string? CameraModel { get; init; }

    public string? InteriorOrientationJson { get; init; }

    public string? ExteriorOrientationJson { get; init; }

    public string? RpcJson { get; init; }

    public string? DemSource { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public readonly record struct RasterExtent
{
    public required double XMin { get; init; }

    public required double YMin { get; init; }

    public required double XMax { get; init; }

    public required double YMax { get; init; }

    public int? Srid { get; init; }
}
