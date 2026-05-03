// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

internal sealed record ElevationValueResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("elevation")]
    public double? Elevation { get; init; }

    [JsonPropertyName("noData")]
    public required bool NoData { get; init; }

    [JsonPropertyName("outOfBounds")]
    public required bool OutOfBounds { get; init; }

    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("querySrid")]
    public int? QuerySrid { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("source")]
    public required ElevationSourceMetadata Source { get; init; }
}

internal sealed record ElevationProfileResponse
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("sampleCount")]
    public required int SampleCount { get; init; }

    [JsonPropertyName("lineLengthMeters")]
    public required double LineLengthMeters { get; init; }

    [JsonPropertyName("lineSrid")]
    public required int LineSrid { get; init; }

    [JsonPropertyName("mosaicRule")]
    public required string MosaicRule { get; init; }

    [JsonPropertyName("isAllNoData")]
    public required bool IsAllNoData { get; init; }

    [JsonPropertyName("samples")]
    public required ElevationProfileSample[] Samples { get; init; }

    [JsonPropertyName("source")]
    public required ElevationSourceMetadata Source { get; init; }
}

internal sealed record ElevationProfileSample
{
    [JsonPropertyName("distanceMeters")]
    public required double DistanceMeters { get; init; }

    [JsonPropertyName("elevation")]
    public double? Elevation { get; init; }

    [JsonPropertyName("noData")]
    public required bool NoData { get; init; }
}

internal sealed record ElevationSourceMetadata
{
    [JsonPropertyName("rasterIds")]
    public required long[] RasterIds { get; init; }

    [JsonPropertyName("rasterCount")]
    public required int RasterCount { get; init; }

    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; init; }

    [JsonPropertyName("sourceCrs")]
    public string? SourceCrs { get; init; }

    [JsonPropertyName("pixelType")]
    public string? PixelType { get; init; }

    [JsonPropertyName("noDataValue")]
    public double? NoDataValue { get; init; }

    [JsonPropertyName("verticalUnit")]
    public string? VerticalUnit { get; init; }

    [JsonPropertyName("verticalDatum")]
    public string? VerticalDatum { get; init; }

    [JsonPropertyName("verticalUnitAssumption")]
    public string VerticalUnitAssumption { get; init; } =
        "Source values are assumed to be meters when no vertical unit is declared.";

    [JsonPropertyName("band")]
    public int Band { get; init; } = 1;
}
