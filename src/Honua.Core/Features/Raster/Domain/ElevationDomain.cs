// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Result of an elevation point query against a registered raster dataset.
/// </summary>
public sealed record ElevationPointResult
{
    /// <summary>
    /// Sampled elevation value at the requested point. <c>null</c> when the
    /// point is outside the raster coverage or falls on a no-data pixel.
    /// </summary>
    public double? Elevation { get; init; }

    /// <summary>
    /// Whether the result represents a no-data pixel or a point outside the
    /// raster coverage.
    /// </summary>
    public required bool NoData { get; init; }

    /// <summary>
    /// Whether the requested point falls outside any registered raster extent.
    /// Implies <see cref="NoData"/>.
    /// </summary>
    public required bool OutOfBounds { get; init; }

    /// <summary>
    /// Layer that owns the elevation source.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Raster identifiers selected for the query. Empty when the point is out
    /// of bounds for the entire dataset.
    /// </summary>
    public required long[] RasterIds { get; init; }

    /// <summary>
    /// Requested X coordinate, echoed back in the result.
    /// </summary>
    public required double X { get; init; }

    /// <summary>
    /// Requested Y coordinate, echoed back in the result.
    /// </summary>
    public required double Y { get; init; }

    /// <summary>
    /// Requested coordinate SRID resolved by the spatial reference registry.
    /// </summary>
    public int? QuerySrid { get; init; }

    /// <summary>
    /// Source raster CRS when consistent across the dataset.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Source pixel type when consistent across the dataset.
    /// </summary>
    public string? PixelType { get; init; }

    /// <summary>
    /// Source no-data value when declared and consistent across the dataset.
    /// </summary>
    public double? NoDataValue { get; init; }

    /// <summary>
    /// Source vertical unit when declared by the raster catalog. <c>null</c>
    /// indicates the dataset does not declare a vertical unit; values are
    /// assumed to be meters.
    /// </summary>
    public string? VerticalUnit { get; init; }

    /// <summary>
    /// Source vertical datum when declared by the raster catalog.
    /// </summary>
    public string? VerticalDatum { get; init; }
}

/// <summary>
/// A single distance/elevation sample in an elevation profile result.
/// </summary>
public readonly record struct ElevationSample
{
    /// <summary>
    /// Distance from the start of the line in meters along the geodesic arc.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Elevation value at this sample. <c>null</c> when the sample falls on
    /// a no-data pixel or outside the raster coverage.
    /// </summary>
    public double? Elevation { get; init; }

    /// <summary>
    /// Whether the sample represents a no-data pixel or a point outside the
    /// raster coverage.
    /// </summary>
    public required bool NoData { get; init; }
}

/// <summary>
/// Result of an elevation profile query along a LineString geometry.
/// </summary>
public sealed record ElevationProfileResult
{
    /// <summary>
    /// Ordered samples from the start of the line to the end.
    /// </summary>
    public required ElevationSample[] Samples { get; init; }

    /// <summary>
    /// Total geodesic length of the input line in meters.
    /// </summary>
    public required double LineLengthMeters { get; init; }

    /// <summary>
    /// Number of samples included in the result. Equals the length of
    /// <see cref="Samples"/>.
    /// </summary>
    public required int SampleCount { get; init; }

    /// <summary>
    /// Layer that owns the elevation source.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Raster identifiers selected for the query.
    /// </summary>
    public required long[] RasterIds { get; init; }

    /// <summary>
    /// Source raster CRS when consistent across the dataset.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Source pixel type when consistent across the dataset.
    /// </summary>
    public string? PixelType { get; init; }

    /// <summary>
    /// Source no-data value when declared and consistent across the dataset.
    /// </summary>
    public double? NoDataValue { get; init; }

    /// <summary>
    /// Source vertical unit when declared. <c>null</c> indicates meters by
    /// assumption.
    /// </summary>
    public string? VerticalUnit { get; init; }

    /// <summary>
    /// Source vertical datum when declared.
    /// </summary>
    public string? VerticalDatum { get; init; }

    /// <summary>
    /// Whether every sample resolved to a no-data or out-of-bounds value.
    /// </summary>
    public required bool IsAllNoData { get; init; }
}

/// <summary>
/// Sampling options for an elevation profile query.
/// </summary>
public readonly record struct ProfileSamplingOptions
{
    /// <summary>
    /// Number of samples (including both endpoints) to compute along the
    /// profile line. Must be at least 2.
    /// </summary>
    public required int SampleCount { get; init; }
}

/// <summary>
/// Failure categories for elevation queries.
/// </summary>
public enum ElevationFailureKind
{
    /// <summary>
    /// No raster source is registered for the requested layer.
    /// </summary>
    SourceUnavailable = 0,

    /// <summary>
    /// The requested coordinate reference system is not supported.
    /// </summary>
    UnsupportedCrs = 1,

    /// <summary>
    /// The supplied geometry is invalid or not the expected type.
    /// </summary>
    InvalidGeometry = 2
}

/// <summary>
/// Exception raised when an elevation query cannot be fulfilled.
/// </summary>
public sealed class ElevationQueryException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ElevationQueryException"/> class.
    /// </summary>
    public ElevationQueryException(ElevationFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    /// <summary>
    /// Failure category.
    /// </summary>
    public ElevationFailureKind FailureKind { get; }
}
