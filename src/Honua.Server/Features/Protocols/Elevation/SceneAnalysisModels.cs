// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

/// <summary>
/// Request body for a sun/shadow analysis.
/// </summary>
internal sealed record SunShadowRequest
{
    /// <summary>Observer longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    /// <summary>Observer latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    /// <summary>Height of the object casting the shadow, above the terrain, in meters.</summary>
    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    /// <summary>UTC instant for which the solar position is computed (ISO 8601).</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Maximum distance to trace the shadow ray, in meters.</summary>
    [JsonPropertyName("maxShadowLengthMeters")]
    public double? MaxShadowLengthMeters { get; init; }

    /// <summary>Requested number of samples along the shadow ray.</summary>
    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    /// <summary>Optional mosaic rule override.</summary>
    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

/// <summary>
/// Solar position metadata for a sun/shadow analysis.
/// </summary>
internal sealed record SolarPositionDto
{
    [JsonPropertyName("altitudeDegrees")]
    public required double AltitudeDegrees { get; init; }

    [JsonPropertyName("azimuthDegrees")]
    public required double AzimuthDegrees { get; init; }

    [JsonPropertyName("declinationDegrees")]
    public required double DeclinationDegrees { get; init; }

    [JsonPropertyName("equationOfTimeMinutes")]
    public required double EquationOfTimeMinutes { get; init; }

    [JsonPropertyName("hourAngleDegrees")]
    public required double HourAngleDegrees { get; init; }

    [JsonPropertyName("aboveHorizon")]
    public required bool AboveHorizon { get; init; }
}

/// <summary>
/// A single sample along the cast shadow ray.
/// </summary>
internal sealed record ShadowSampleDto
{
    [JsonPropertyName("lon")]
    public required double Lon { get; init; }

    [JsonPropertyName("lat")]
    public required double Lat { get; init; }

    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }

    [JsonPropertyName("terrainElevation")]
    public double? TerrainElevation { get; init; }

    [JsonPropertyName("rayElevation")]
    public required double RayElevation { get; init; }
}

/// <summary>
/// Response for a sun/shadow analysis.
/// </summary>
internal sealed record SunShadowResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("solarPosition")]
    public required SolarPositionDto SolarPosition { get; init; }

    [JsonPropertyName("shadowCast")]
    public required bool ShadowCast { get; init; }

    [JsonPropertyName("noShadowReason")]
    public string? NoShadowReason { get; init; }

    [JsonPropertyName("observerGroundElevation")]
    public required double ObserverGroundElevation { get; init; }

    [JsonPropertyName("observerTopElevation")]
    public required double ObserverTopElevation { get; init; }

    [JsonPropertyName("shadowAzimuthDegrees")]
    public required double ShadowAzimuthDegrees { get; init; }

    [JsonPropertyName("shadowLengthMeters")]
    public required double ShadowLengthMeters { get; init; }

    [JsonPropertyName("tipLon")]
    public double? TipLon { get; init; }

    [JsonPropertyName("tipLat")]
    public double? TipLat { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("samples")]
    public required ShadowSampleDto[] Samples { get; init; }
}

/// <summary>
/// Request body for a slice/volumetric cross-section analysis.
/// </summary>
internal sealed record SliceRequest
{
    /// <summary>Slice start longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("startLon")]
    public double? StartLon { get; init; }

    /// <summary>Slice start latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("startLat")]
    public double? StartLat { get; init; }

    /// <summary>Slice end longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("endLon")]
    public double? EndLon { get; init; }

    /// <summary>Slice end latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("endLat")]
    public double? EndLat { get; init; }

    /// <summary>Requested number of samples along the slice plane.</summary>
    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    /// <summary>Optional mosaic rule override.</summary>
    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

/// <summary>
/// A single intersection sample along a slice plane.
/// </summary>
internal sealed record SliceSampleDto
{
    [JsonPropertyName("lon")]
    public required double Lon { get; init; }

    [JsonPropertyName("lat")]
    public required double Lat { get; init; }

    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }

    [JsonPropertyName("elevation")]
    public double? Elevation { get; init; }
}

/// <summary>
/// Response for a slice/volumetric cross-section analysis.
/// </summary>
internal sealed record SliceResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("lengthMeters")]
    public required double LengthMeters { get; init; }

    [JsonPropertyName("sampleCount")]
    public required int SampleCount { get; init; }

    [JsonPropertyName("minElevation")]
    public double? MinElevation { get; init; }

    [JsonPropertyName("maxElevation")]
    public double? MaxElevation { get; init; }

    [JsonPropertyName("reliefMeters")]
    public double? ReliefMeters { get; init; }

    [JsonPropertyName("hasNoDataSamples")]
    public required bool HasNoDataSamples { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("samples")]
    public required SliceSampleDto[] Samples { get; init; }
}
