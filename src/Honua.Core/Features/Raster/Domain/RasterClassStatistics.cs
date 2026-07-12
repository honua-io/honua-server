// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// The aligned per-pixel band vectors read from a raster (or mosaic) inside an
/// area-of-interest clip geometry. Each entry in <see cref="Pixels"/> is one valid
/// pixel: a vector of that pixel's value in each band, ordered to match <see cref="Bands"/>.
/// Only pixels that carry a valid (non-NoData) value in <b>every</b> requested band are
/// included, so the vectors can be fed directly into a multivariate covariance calculation.
/// </summary>
/// <remarks>
/// The read is bounded: an implementation must never materialize more than the caller's
/// pixel budget. When the clip's bounding box exceeds that budget the implementation returns
/// <see cref="ExceededPixelBudget"/> = <see langword="true"/> with an empty <see cref="Pixels"/>
/// rather than silently truncating (which would corrupt the covariance signature). Streaming or
/// tiling the read to lift the budget is deferred follow-up scope.
/// </remarks>
public sealed class RasterBandVectorSet
{
    /// <summary>
    /// 1-based band numbers, in the order each component of a pixel vector appears.
    /// </summary>
    public required int[] Bands { get; init; }

    /// <summary>
    /// One vector per valid pixel; each vector has <see cref="Bands"/>.Length components.
    /// </summary>
    public required IReadOnlyList<double[]> Pixels { get; init; }

    /// <summary>
    /// <see langword="true"/> when the clip bounding box exceeds the caller's pixel budget,
    /// in which case <see cref="Pixels"/> is empty and the caller must reject the request
    /// rather than report a partial signature.
    /// </summary>
    public bool ExceededPixelBudget { get; init; }

    /// <summary>
    /// Number of pixels in the clip bounding box (width * height of the clipped raster),
    /// reported so callers can surface a precise "AOI too large" diagnostic.
    /// </summary>
    public long BoundingPixelCount { get; init; }

    /// <summary>An empty result (no rasters intersected the clip, or the clip was empty).</summary>
    public static RasterBandVectorSet Empty(int[] bands) => new()
    {
        Bands = bands,
        Pixels = Array.Empty<double[]>(),
    };
}

/// <summary>
/// One class description in a class-statistics request: a class identity plus the training
/// area-of-interest whose pixels define the class signature.
/// </summary>
public sealed class RasterClassAoi
{
    /// <summary>Caller-assigned class identifier echoed back on the signature.</summary>
    public required int ClassId { get; init; }

    /// <summary>Optional human-readable class name.</summary>
    public string? Name { get; init; }

    /// <summary>Training AOI geometry encoded as Well-Known Binary.</summary>
    public required byte[] ClipGeometry { get; init; }

    /// <summary>SRID of <see cref="ClipGeometry"/>; <see langword="null"/> assumes the raster SRID.</summary>
    public int? ClipSrid { get; init; }
}

/// <summary>
/// Protocol-neutral request to compute per-class statistical signatures over a raster
/// selection. Each class in <see cref="Classes"/> contributes the pixels inside its training
/// AOI; the resulting signature is the class pixel count, per-band mean vector, and band-by-band
/// covariance matrix used by maximum-likelihood classifiers.
/// </summary>
public sealed class RasterClassStatisticsRequest
{
    /// <summary>Layer whose rasters are analysed.</summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Catalog raster ids to analyse. A single id analyses one raster; multiple ids composite a
    /// mosaic using <see cref="MergeStrategy"/> before pixels are read.
    /// </summary>
    public required long[] RasterIds { get; init; }

    /// <summary>Pixel-resolution operation applied to overlapping mosaic pixels.</summary>
    public RasterMergeStrategy MergeStrategy { get; init; } = RasterMergeStrategy.Newest;

    /// <summary>Optional 1-based band selection; <see langword="null"/> analyses every band.</summary>
    public int[]? Bands { get; init; }

    /// <summary>Class descriptions with their training AOIs.</summary>
    public required IReadOnlyList<RasterClassAoi> Classes { get; init; }

    /// <summary>
    /// Maximum number of pixels read per class AOI. A class whose clip bounding box exceeds this
    /// budget is rejected rather than partially analysed.
    /// </summary>
    public required int MaxPixelsPerClass { get; init; }
}

/// <summary>Per-band univariate summary of the pixels in a class AOI.</summary>
public readonly record struct RasterClassBandSummary
{
    /// <summary>1-based band number.</summary>
    public required int Band { get; init; }

    /// <summary>Minimum pixel value in the class AOI.</summary>
    public required double Min { get; init; }

    /// <summary>Maximum pixel value in the class AOI.</summary>
    public required double Max { get; init; }

    /// <summary>Mean pixel value in the class AOI.</summary>
    public required double Mean { get; init; }

    /// <summary>
    /// Sample standard deviation of the band (square root of the covariance diagonal,
    /// divided by n-1). Zero when the class has fewer than two pixels.
    /// </summary>
    public required double StandardDeviation { get; init; }
}

/// <summary>
/// The statistical signature of one class: pixel count, per-band mean vector, band-by-band
/// covariance matrix, and per-band summaries.
/// </summary>
public sealed class RasterClassSignature
{
    /// <summary>Caller-assigned class identifier.</summary>
    public required int ClassId { get; init; }

    /// <summary>Optional class name echoed from the request.</summary>
    public string? Name { get; init; }

    /// <summary>Number of valid pixels contributing to the signature.</summary>
    public required long PixelCount { get; init; }

    /// <summary>1-based band numbers, in the order the mean/covariance components appear.</summary>
    public required int[] Bands { get; init; }

    /// <summary>Per-band mean value (the class centroid in band space).</summary>
    public required double[] Mean { get; init; }

    /// <summary>
    /// Band-by-band sample covariance matrix (divided by n-1). Symmetric and
    /// <see cref="Bands"/>.Length square. All zero when the class has fewer than two pixels.
    /// </summary>
    public required double[][] Covariance { get; init; }

    /// <summary>Per-band univariate summaries.</summary>
    public required RasterClassBandSummary[] BandSummaries { get; init; }
}

/// <summary>The result of a class-statistics computation: one signature per requested class.</summary>
public sealed class RasterClassStatisticsResult
{
    /// <summary>Per-class signatures, in request order.</summary>
    public required IReadOnlyList<RasterClassSignature> Signatures { get; init; }
}
