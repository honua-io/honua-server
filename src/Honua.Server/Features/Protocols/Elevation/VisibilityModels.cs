// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

/// <summary>
/// Request body for a line-of-sight analysis between an observer and a target.
/// </summary>
internal sealed record LineOfSightRequest
{
    /// <summary>Observer longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    /// <summary>Observer latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    /// <summary>Observer height offset above the terrain surface, in meters.</summary>
    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    /// <summary>Target longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("targetLon")]
    public double? TargetLon { get; init; }

    /// <summary>Target latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("targetLat")]
    public double? TargetLat { get; init; }

    /// <summary>Target height offset above the terrain surface, in meters.</summary>
    [JsonPropertyName("targetHeight")]
    public double? TargetHeight { get; init; }

    /// <summary>Requested number of profile samples along the sight line.</summary>
    [JsonPropertyName("sampleCount")]
    public int? SampleCount { get; init; }

    /// <summary>Optional mosaic rule override.</summary>
    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

/// <summary>
/// Response for a line-of-sight analysis.
/// </summary>
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

/// <summary>
/// First terrain obstruction along a sight line.
/// </summary>
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

/// <summary>
/// Request body for a viewshed analysis around an observer.
/// </summary>
internal sealed record ViewshedRequest
{
    /// <summary>Observer longitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLon")]
    public double? ObserverLon { get; init; }

    /// <summary>Observer latitude in WGS 84 decimal degrees.</summary>
    [JsonPropertyName("observerLat")]
    public double? ObserverLat { get; init; }

    /// <summary>Observer height offset above the terrain surface, in meters.</summary>
    [JsonPropertyName("observerHeight")]
    public double? ObserverHeight { get; init; }

    /// <summary>Height offset applied to each sampled target point, in meters.</summary>
    [JsonPropertyName("targetHeight")]
    public double? TargetHeight { get; init; }

    /// <summary>Analysis radius in meters.</summary>
    [JsonPropertyName("radiusMeters")]
    public double? RadiusMeters { get; init; }

    /// <summary>Number of azimuth rays cast around the observer.</summary>
    [JsonPropertyName("rayCount")]
    public int? RayCount { get; init; }

    /// <summary>Number of range samples per ray.</summary>
    [JsonPropertyName("samplesPerRay")]
    public int? SamplesPerRay { get; init; }

    /// <summary>Optional mosaic rule override.</summary>
    [JsonPropertyName("mosaicRule")]
    public string? MosaicRule { get; init; }
}

/// <summary>
/// Response for a viewshed analysis.
/// </summary>
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

/// <summary>
/// A single radial viewshed sample.
/// </summary>
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
