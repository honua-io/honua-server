// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

internal sealed record SunShadowRequest
{
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("maxShadowLengthMeters")]
    public double? MaxShadowLengthMeters { get; init; }

    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

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

internal sealed record SliceRequest
{
    [JsonPropertyName("startLon")]
    public double? StartLon { get; init; }

    [JsonPropertyName("startLat")]
    public double? StartLat { get; init; }

    [JsonPropertyName("endLon")]
    public double? EndLon { get; init; }

    [JsonPropertyName("endLat")]
    public double? EndLat { get; init; }

    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

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
