// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IRasterClassStatisticsAnalyzer
{
    Task<RasterClassStatisticsResult> ComputeAsync(
        RasterClassStatisticsRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RasterClassStatisticsAoiTooLargeException : Exception
{
    public RasterClassStatisticsAoiTooLargeException(int classId, long boundingPixelCount, int maxPixels)
        : base($"Class {classId} training AOI spans {boundingPixelCount} pixels, exceeding the " +
               $"per-class analysis budget of {maxPixels}. Reduce the AOI size or select fewer bands.")
    {
        ClassId = classId;
        BoundingPixelCount = boundingPixelCount;
        MaxPixels = maxPixels;
    }

    public int ClassId { get; }

    public long BoundingPixelCount { get; }

    public int MaxPixels { get; }
}
