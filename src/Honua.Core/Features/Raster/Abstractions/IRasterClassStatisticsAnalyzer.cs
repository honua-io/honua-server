// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Computes per-class statistical signatures (count, per-band mean vector, band-by-band
/// covariance, and per-band summaries) for a validated <see cref="RasterClassStatisticsRequest"/>,
/// reading the training-AOI pixels through the shared <see cref="IRasterStore"/> analytics path.
/// </summary>
public interface IRasterClassStatisticsAnalyzer
{
    /// <summary>
    /// Computes one signature per requested class.
    /// </summary>
    /// <exception cref="RasterClassStatisticsAoiTooLargeException">
    /// Thrown when a class AOI's clip bounding box exceeds the request's per-class pixel budget,
    /// so the caller rejects the request rather than returning a truncated signature.
    /// </exception>
    Task<RasterClassStatisticsResult> ComputeAsync(
        RasterClassStatisticsRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a class training AOI is too large to analyse within the configured per-class
/// pixel budget. Carries the offending class id and the pixel counts so the protocol layer can
/// surface a precise, honest rejection instead of a partial signature.
/// </summary>
public sealed class RasterClassStatisticsAoiTooLargeException : Exception
{
    /// <summary>Creates the exception for a class whose AOI exceeded the pixel budget.</summary>
    public RasterClassStatisticsAoiTooLargeException(int classId, long boundingPixelCount, int maxPixels)
        : base($"Class {classId} training AOI spans {boundingPixelCount} pixels, exceeding the " +
               $"per-class analysis budget of {maxPixels}. Reduce the AOI size or select fewer bands.")
    {
        ClassId = classId;
        BoundingPixelCount = boundingPixelCount;
        MaxPixels = maxPixels;
    }

    /// <summary>The class whose AOI exceeded the budget.</summary>
    public int ClassId { get; }

    /// <summary>Number of pixels in the class AOI clip bounding box.</summary>
    public long BoundingPixelCount { get; }

    /// <summary>The configured per-class pixel budget.</summary>
    public int MaxPixels { get; }
}
