// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ComputeHistogramsResponse
{
    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

public sealed class ComputeCacheInfoResponse
{
    [JsonPropertyName("cacheInfo")]
    public required ImageServerComputedCacheInfo CacheInfo { get; init; }
}

public sealed class ImageServerComputedCacheInfo
{
    [JsonPropertyName("tileInfo")]
    public TileInfo? TileInfo { get; init; }

    [JsonPropertyName("extent")]
    public required ImageServerExtent Extent { get; init; }

    [JsonPropertyName("cacheType")]
    public string? CacheType { get; init; }
}

public sealed class ComputePixelLocationResponse
{
    [JsonPropertyName("geometries")]
    public PixelLocationPoint[] Geometries { get; init; } = [];
}

public sealed class PixelLocationPoint
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }
}

public sealed class QueryBoundaryResponse
{
    [JsonPropertyName("shape")]
    public required CatalogQueryGeometry Shape { get; init; }

    [JsonPropertyName("area")]
    public required double Area { get; init; }
}

public sealed class ImageServerProjectResponse
{
    [JsonPropertyName("geometries")]
    public JsonElement[] Geometries { get; init; } = [];
}

public sealed class GetSamplesResponse
{
    [JsonPropertyName("samples")]
    public SampleEntry[] Samples { get; init; } = [];
}

public sealed class SampleEntry
{
    [JsonPropertyName("rasterId")]
    public long? RasterId { get; init; }

    [JsonPropertyName("location")]
    public required SampleLocation Location { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("resolution")]
    public double? Resolution { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }
}

public sealed class SampleLocation
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}

public sealed class KeyPropertiesResponse
{
    [JsonPropertyName("BandProperties")]
    public BandProperty[] BandProperties { get; init; } = [];

    [JsonPropertyName("HighCellSize")]
    public double? HighCellSize { get; init; }

    [JsonPropertyName("LowCellSize")]
    public double? LowCellSize { get; init; }

    [JsonPropertyName("MaxCellSize")]
    public double? MaxCellSize { get; init; }

    [JsonPropertyName("ConfigKeyword")]
    public string ConfigKeyword { get; init; } = string.Empty;

    [JsonPropertyName("BandDefinitionKeyword")]
    public string BandDefinitionKeyword { get; init; } = string.Empty;

    [JsonPropertyName("DataType")]
    public string? DataType { get; init; }

    [JsonPropertyName("BandCount")]
    public int BandCount { get; init; }

    [JsonPropertyName("NoDataValue")]
    public double? NoDataValue { get; init; }
}

public sealed class BandProperty
{
    [JsonPropertyName("BandName")]
    public required string BandName { get; init; }

    [JsonPropertyName("PixelType")]
    public string? PixelType { get; init; }
}
