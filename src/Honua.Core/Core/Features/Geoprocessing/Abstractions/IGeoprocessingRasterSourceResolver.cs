// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IGeoprocessingRasterSourceResolver
{
    Task<RasterSourceLayerResolution> ResolveLayerIdAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default);

    Task<RasterSourceResolution> ResolveAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default);
}

public sealed record RasterSourceReference(int? LayerId, long? RasterId);

public sealed record RasterSourceLayerResolution
{
    public bool Found { get; init; }

    public int? LayerId { get; init; }

    public static RasterSourceLayerResolution Success(int layerId) =>
        new() { Found = true, LayerId = layerId };

    public static RasterSourceLayerResolution NotFound() => new();
}

public sealed record RasterSourceResolution
{
    public bool Found { get; init; }

    public RasterSourceDescriptor? Descriptor { get; init; }

    public string? FailureReason { get; init; }

    public static RasterSourceResolution Success(RasterSourceDescriptor descriptor) =>
        new() { Found = true, Descriptor = descriptor };

    public static RasterSourceResolution Failure(string reason) =>
        new() { Found = false, FailureReason = reason };
}
