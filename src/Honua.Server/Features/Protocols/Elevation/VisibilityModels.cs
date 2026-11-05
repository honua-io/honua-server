// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

internal sealed record LineOfSightRequest
{
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    [JsonPropertyName("targetLon")]
    public double? TargetLon { get; init; }

    [JsonPropertyName("targetLat")]
    public double? TargetLat { get; init; }

    [JsonPropertyName("targetHeight")]
    public double? TargetHeight { get; init; }

    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

internal sealed record LineOfSightResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("visible")]
    public required bool Visible { get; init; }

    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }

    [JsonPropertyName("observerGroundElevation")]
    public required double ObserverGroundElevation { get; init; }

    [JsonPropertyName("observerElevation")]
    public required double ObserverElevation { get; init; }

    [JsonPropertyName("targetGroundElevation")]
    public required double TargetGroundElevation { get; init; }

    [JsonPropertyName("targetElevation")]
    public required double TargetElevation { get; init; }

    [JsonPropertyName("sampleCount")]
    public required int SampleCount { get; init; }

    [JsonPropertyName("hasNoDataSamples")]
    public required bool HasNoDataSamples { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("obstruction")]
    public LineOfSightObstructionDto? Obstruction { get; init; }
}

internal sealed record LineOfSightObstructionDto
{
    [JsonPropertyName("lon")]
    public required double Lon { get; init; }

    [JsonPropertyName("lat")]
    public required double Lat { get; init; }

    [JsonPropertyName("elevation")]
    public required double Elevation { get; init; }

    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }
}

internal sealed record ViewshedRequest
{
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    [JsonPropertyName("targetHeight")]
    public double? TargetHeight { get; init; }

    [JsonPropertyName("radiusMeters")]
    public double? RadiusMeters { get; init; }

    [JsonPropertyName("rayCount")]
    public int? RayCount { get; init; }

    [JsonPropertyName("samplesPerRay")]
    public int? SamplesPerRay { get; init; }

    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

internal sealed record ViewshedResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("observerGroundElevation")]
    public required double ObserverGroundElevation { get; init; }

    [JsonPropertyName("observerElevation")]
    public required double ObserverElevation { get; init; }

    [JsonPropertyName("observerNoData")]
    public required bool ObserverNoData { get; init; }

    [JsonPropertyName("radiusMeters")]
    public required double RadiusMeters { get; init; }

    [JsonPropertyName("rayCount")]
    public required int RayCount { get; init; }

    [JsonPropertyName("samplesPerRay")]
    public required int SamplesPerRay { get; init; }

    [JsonPropertyName("sampleCount")]
    public required int SampleCount { get; init; }

    [JsonPropertyName("visibleSampleCount")]
    public required int VisibleSampleCount { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("samples")]
    public required ViewshedSampleDto[] Samples { get; init; }
}

internal sealed record ViewshedSampleDto
{
    [JsonPropertyName("lon")]
    public required double Lon { get; init; }

    [JsonPropertyName("lat")]
    public required double Lat { get; init; }

    [JsonPropertyName("azimuthDegrees")]
    public required double AzimuthDegrees { get; init; }

    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }

    [JsonPropertyName("elevation")]
    public double? Elevation { get; init; }

    [JsonPropertyName("visible")]
    public required bool Visible { get; init; }
}
