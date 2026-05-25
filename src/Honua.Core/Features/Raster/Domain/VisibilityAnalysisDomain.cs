// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// A geographic observer or target position used by visibility analysis,
/// expressed in WGS 84 longitude/latitude with an optional height offset above
/// the sampled terrain surface (in meters).
/// </summary>
public readonly record struct VisibilityPoint
{
    /// <summary>
    /// Longitude in WGS 84 decimal degrees.
    /// </summary>
    public required double Longitude { get; init; }

    /// <summary>
    /// Latitude in WGS 84 decimal degrees.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Height offset above the terrain surface, in meters. Defaults to 0.
    /// For an observer this models antenna/eye height; for a target it models
    /// the height of the object being observed.
    /// </summary>
    public double HeightOffsetMeters { get; init; }
}

/// <summary>
/// The first point along a line of sight where the terrain surface rises above
/// the sight line, blocking visibility.
/// </summary>
public sealed record LineOfSightObstruction
{
    /// <summary>
    /// Longitude of the obstruction in WGS 84 decimal degrees.
    /// </summary>
    public required double Longitude { get; init; }

    /// <summary>
    /// Latitude of the obstruction in WGS 84 decimal degrees.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Terrain elevation at the obstruction, in meters.
    /// </summary>
    public required double Elevation { get; init; }

    /// <summary>
    /// Distance from the observer to the obstruction, in meters, measured along
    /// the geodesic arc.
    /// </summary>
    public required double DistanceMeters { get; init; }
}

/// <summary>
/// Result of a line-of-sight analysis between an observer and a target.
/// </summary>
public sealed record LineOfSightResult
{
    /// <summary>
    /// Layer that owns the elevation source.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Whether the target is visible from the observer with no terrain
    /// obstruction along the sight line.
    /// </summary>
    public required bool Visible { get; init; }

    /// <summary>
    /// First terrain obstruction along the sight line, or <c>null</c> when the
    /// target is visible.
    /// </summary>
    public LineOfSightObstruction? Obstruction { get; init; }

    /// <summary>
    /// Terrain elevation at the observer position, in meters.
    /// </summary>
    public required double ObserverGroundElevation { get; init; }

    /// <summary>
    /// Absolute observer elevation including the height offset, in meters.
    /// </summary>
    public required double ObserverElevation { get; init; }

    /// <summary>
    /// Terrain elevation at the target position, in meters.
    /// </summary>
    public required double TargetGroundElevation { get; init; }

    /// <summary>
    /// Absolute target elevation including the height offset, in meters.
    /// </summary>
    public required double TargetElevation { get; init; }

    /// <summary>
    /// Geodesic distance from observer to target, in meters.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Number of profile samples evaluated along the sight line.
    /// </summary>
    public required int SampleCount { get; init; }

    /// <summary>
    /// Whether one or more profile samples between observer and target resolved
    /// to a no-data pixel. Visibility is reported on a best-effort basis when
    /// true: no-data samples are skipped during the obstruction test.
    /// </summary>
    public required bool HasNoDataSamples { get; init; }
}

/// <summary>
/// A single radial sample produced by a viewshed analysis.
/// </summary>
public readonly record struct ViewshedSample
{
    /// <summary>
    /// Longitude of the sample in WGS 84 decimal degrees.
    /// </summary>
    public required double Longitude { get; init; }

    /// <summary>
    /// Latitude of the sample in WGS 84 decimal degrees.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Azimuth from the observer to the sample, in degrees clockwise from north
    /// (0 = north, 90 = east).
    /// </summary>
    public required double AzimuthDegrees { get; init; }

    /// <summary>
    /// Distance from the observer to the sample, in meters.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Terrain elevation at the sample, in meters. <c>null</c> when the sample
    /// resolved to a no-data pixel.
    /// </summary>
    public double? Elevation { get; init; }

    /// <summary>
    /// Whether this sample is visible from the observer.
    /// </summary>
    public required bool Visible { get; init; }
}

/// <summary>
/// Result of a viewshed analysis: the set of radially-sampled points around an
/// observer, each flagged visible or obstructed.
/// </summary>
public sealed record ViewshedResult
{
    /// <summary>
    /// Layer that owns the elevation source.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Terrain elevation at the observer position, in meters.
    /// </summary>
    public required double ObserverGroundElevation { get; init; }

    /// <summary>
    /// Absolute observer elevation including the height offset, in meters.
    /// </summary>
    public required double ObserverElevation { get; init; }

    /// <summary>
    /// Analysis radius in meters.
    /// </summary>
    public required double RadiusMeters { get; init; }

    /// <summary>
    /// Number of azimuth rays cast around the observer.
    /// </summary>
    public required int RayCount { get; init; }

    /// <summary>
    /// Number of range samples per ray (excluding the observer position).
    /// </summary>
    public required int SamplesPerRay { get; init; }

    /// <summary>
    /// All radial samples, ordered by azimuth then range.
    /// </summary>
    public required ViewshedSample[] Samples { get; init; }

    /// <summary>
    /// Count of samples flagged visible.
    /// </summary>
    public required int VisibleSampleCount { get; init; }

    /// <summary>
    /// Whether the observer position itself resolved to a no-data pixel, which
    /// makes the analysis indeterminate.
    /// </summary>
    public required bool ObserverNoData { get; init; }
}

/// <summary>
/// Options controlling a viewshed analysis.
/// </summary>
public readonly record struct ViewshedOptions
{
    /// <summary>
    /// Analysis radius in meters. Must be strictly positive.
    /// </summary>
    public required double RadiusMeters { get; init; }

    /// <summary>
    /// Number of azimuth rays cast around the observer. Clamped to
    /// <c>[MinRayCount, MaxRayCount]</c>.
    /// </summary>
    public int? RayCount { get; init; }

    /// <summary>
    /// Number of range samples per ray. Clamped to
    /// <c>[2, MaxSamplesPerRay]</c>.
    /// </summary>
    public int? SamplesPerRay { get; init; }

    /// <summary>
    /// Observer height offset above the terrain surface, in meters.
    /// </summary>
    public double ObserverHeightOffsetMeters { get; init; }

    /// <summary>
    /// Height offset applied to every sampled target point, in meters.
    /// </summary>
    public double TargetHeightOffsetMeters { get; init; }
}
