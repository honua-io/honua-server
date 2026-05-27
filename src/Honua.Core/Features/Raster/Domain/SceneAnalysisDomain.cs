// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// The position of the sun in the local sky for an observer, computed from a
/// UTC instant and a WGS 84 longitude/latitude using the NOAA solar position
/// algorithm.
/// </summary>
public readonly record struct SolarPosition
{
    /// <summary>
    /// Solar altitude (elevation) above the horizon, in degrees. Negative when
    /// the sun is below the horizon. Not corrected for atmospheric refraction.
    /// </summary>
    public required double AltitudeDegrees { get; init; }

    /// <summary>
    /// Solar azimuth in degrees clockwise from true north (0 = north, 90 = east,
    /// 180 = south, 270 = west).
    /// </summary>
    public required double AzimuthDegrees { get; init; }

    /// <summary>
    /// Solar declination in degrees for the instant.
    /// </summary>
    public required double DeclinationDegrees { get; init; }

    /// <summary>
    /// Equation of time in minutes for the instant.
    /// </summary>
    public required double EquationOfTimeMinutes { get; init; }

    /// <summary>
    /// Hour angle of the sun in degrees (0 at solar noon, negative before,
    /// positive after).
    /// </summary>
    public required double HourAngleDegrees { get; init; }

    /// <summary>
    /// Whether the sun is above the horizon (<see cref="AltitudeDegrees"/> &gt; 0).
    /// </summary>
    public bool IsAboveHorizon => AltitudeDegrees > 0;
}

/// <summary>
/// A geographic observer used by a sun/shadow analysis, expressed in WGS 84
/// longitude/latitude with an optional height above the terrain surface (in
/// meters) — for example a structure or pole whose shadow is being cast.
/// </summary>
public readonly record struct ShadowObserver
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
    /// Height of the object above the terrain surface, in meters. The shadow is
    /// cast from the top of this object. Defaults to 0.
    /// </summary>
    public double HeightMeters { get; init; }
}

/// <summary>
/// A single sample along the cast shadow, walking away from the observer in the
/// anti-solar direction until the falling shadow ray meets the terrain surface.
/// </summary>
public readonly record struct ShadowSample
{
    /// <summary>Longitude of the sample in WGS 84 decimal degrees.</summary>
    public required double Longitude { get; init; }

    /// <summary>Latitude of the sample in WGS 84 decimal degrees.</summary>
    public required double Latitude { get; init; }

    /// <summary>Distance from the observer to the sample, in meters.</summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Terrain elevation at the sample, in meters. <c>null</c> when the sample
    /// resolved to a no-data pixel.
    /// </summary>
    public double? TerrainElevation { get; init; }

    /// <summary>
    /// Height of the falling shadow ray at this distance, in meters absolute.
    /// </summary>
    public required double RayElevation { get; init; }
}

/// <summary>
/// Result of a sun/shadow analysis: the solar position plus, when the sun is
/// above the horizon, the cast shadow extent against the elevation surface.
/// </summary>
public sealed record SunShadowResult
{
    /// <summary>Layer that owns the elevation source.</summary>
    public required int LayerId { get; init; }

    /// <summary>The computed solar position for the requested instant/location.</summary>
    public required SolarPosition SolarPosition { get; init; }

    /// <summary>
    /// Whether a shadow was cast. <c>false</c> when the sun is at or below the
    /// horizon, in which case <see cref="Samples"/> is empty and
    /// <see cref="ShadowLengthMeters"/> is 0.
    /// </summary>
    public required bool ShadowCast { get; init; }

    /// <summary>
    /// Human-readable explanation when no shadow was cast (for example the sun
    /// being below the horizon), otherwise <c>null</c>.
    /// </summary>
    public string? NoShadowReason { get; init; }

    /// <summary>Terrain elevation at the observer position, in meters.</summary>
    public required double ObserverGroundElevation { get; init; }

    /// <summary>
    /// Absolute elevation of the top of the observer object (ground + height),
    /// in meters.
    /// </summary>
    public required double ObserverTopElevation { get; init; }

    /// <summary>
    /// Azimuth the shadow is cast toward, in degrees clockwise from north
    /// (anti-solar: <c>solarAzimuth + 180</c>). 0 when no shadow is cast.
    /// </summary>
    public required double ShadowAzimuthDegrees { get; init; }

    /// <summary>
    /// Length of the shadow from the observer to the point where the falling
    /// shadow ray meets the terrain, in meters. 0 when no shadow is cast.
    /// </summary>
    public required double ShadowLengthMeters { get; init; }

    /// <summary>
    /// Longitude of the shadow tip in WGS 84 decimal degrees, or <c>null</c>
    /// when no shadow is cast.
    /// </summary>
    public double? TipLongitude { get; init; }

    /// <summary>
    /// Latitude of the shadow tip in WGS 84 decimal degrees, or <c>null</c> when
    /// no shadow is cast.
    /// </summary>
    public double? TipLatitude { get; init; }

    /// <summary>
    /// Samples along the shadow ray from the observer to the shadow tip, ordered
    /// by increasing distance. Empty when no shadow is cast.
    /// </summary>
    public required ShadowSample[] Samples { get; init; }
}

/// <summary>
/// Options controlling a sun/shadow analysis.
/// </summary>
public readonly record struct SunShadowOptions
{
    /// <summary>UTC instant for which the solar position is computed.</summary>
    public required DateTimeOffset InstantUtc { get; init; }

    /// <summary>
    /// Maximum distance the shadow ray is traced before giving up, in meters.
    /// Must be strictly positive.
    /// </summary>
    public required double MaxShadowLengthMeters { get; init; }

    /// <summary>
    /// Number of range samples taken along the shadow ray. Clamped to
    /// <c>[2, MaxShadowSamples]</c>.
    /// </summary>
    public int? SampleCount { get; init; }
}

/// <summary>
/// Definition of a vertical slice plane used by a cross-section analysis. The
/// plane is anchored by a start and end coordinate and is extruded vertically,
/// so the surface intersection is the terrain profile along the start→end line.
/// </summary>
public readonly record struct SlicePlane
{
    /// <summary>Start longitude in WGS 84 decimal degrees.</summary>
    public required double StartLongitude { get; init; }

    /// <summary>Start latitude in WGS 84 decimal degrees.</summary>
    public required double StartLatitude { get; init; }

    /// <summary>End longitude in WGS 84 decimal degrees.</summary>
    public required double EndLongitude { get; init; }

    /// <summary>End latitude in WGS 84 decimal degrees.</summary>
    public required double EndLatitude { get; init; }
}

/// <summary>
/// A single sample where the slice plane intersects the terrain surface.
/// </summary>
public readonly record struct SliceSample
{
    /// <summary>Longitude of the sample in WGS 84 decimal degrees.</summary>
    public required double Longitude { get; init; }

    /// <summary>Latitude of the sample in WGS 84 decimal degrees.</summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Distance from the slice start along the plane, in meters.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Terrain elevation where the plane meets the surface, in meters.
    /// <c>null</c> when the sample resolved to a no-data pixel.
    /// </summary>
    public double? Elevation { get; init; }
}

/// <summary>
/// Result of a slice/volumetric cross-section analysis: the polyline where the
/// slice plane intersects the terrain surface plus summary metadata.
/// </summary>
public sealed record SliceResult
{
    /// <summary>Layer that owns the elevation source.</summary>
    public required int LayerId { get; init; }

    /// <summary>Length of the slice line along the surface, in meters.</summary>
    public required double LengthMeters { get; init; }

    /// <summary>Number of intersection samples produced.</summary>
    public required int SampleCount { get; init; }

    /// <summary>
    /// Minimum terrain elevation along the intersection, in meters. <c>null</c>
    /// when every sample was no-data.
    /// </summary>
    public double? MinElevation { get; init; }

    /// <summary>
    /// Maximum terrain elevation along the intersection, in meters. <c>null</c>
    /// when every sample was no-data.
    /// </summary>
    public double? MaxElevation { get; init; }

    /// <summary>
    /// Elevation relief (max minus min) along the intersection, in meters.
    /// <c>null</c> when every sample was no-data.
    /// </summary>
    public double? ReliefMeters { get; init; }

    /// <summary>
    /// Whether one or more samples along the slice resolved to a no-data pixel.
    /// </summary>
    public required bool HasNoDataSamples { get; init; }

    /// <summary>
    /// Intersection samples ordered by increasing distance from the slice start.
    /// </summary>
    public required SliceSample[] Samples { get; init; }
}
