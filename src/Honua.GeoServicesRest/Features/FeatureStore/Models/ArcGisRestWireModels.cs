// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.GeoServicesRest.Features.FeatureStore.Models;

// JSON wire-format types for ArcGIS REST API responses. Kept intentionally
// minimal — only fields read by the federated read-through provider are present.

#pragma warning disable CA1812 // Internal class is never instantiated (used for JSON deserialization)

internal sealed record GeoServicesRestQueryResponse
{
    [JsonPropertyName("features")]
    public GeoServicesRestFeature[]? Features { get; init; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }

    [JsonPropertyName("spatialReference")]
    public GeoServicesRestSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("objectIdFieldName")]
    public string? ObjectIdFieldName { get; init; }

    [JsonPropertyName("globalIdFieldName")]
    public string? GlobalIdFieldName { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesRestError? Error { get; init; }
}

internal sealed record GeoServicesRestFeature
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, JsonElement>? Attributes { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }
}

internal sealed record GeoServicesRestSpatialReference
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record GeoServicesRestCountResponse
{
    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesRestError? Error { get; init; }
}

internal sealed record GeoServicesRestExtentResponse
{
    [JsonPropertyName("extent")]
    public GeoServicesRestExtent? Extent { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesRestError? Error { get; init; }
}

internal sealed record GeoServicesRestExtent
{
    [JsonPropertyName("xmin")]
    public double Xmin { get; init; }

    [JsonPropertyName("ymin")]
    public double Ymin { get; init; }

    [JsonPropertyName("xmax")]
    public double Xmax { get; init; }

    [JsonPropertyName("ymax")]
    public double Ymax { get; init; }

    [JsonPropertyName("spatialReference")]
    public GeoServicesRestSpatialReference? SpatialReference { get; init; }
}

internal sealed record GeoServicesRestObjectIdsResponse
{
    [JsonPropertyName("objectIdFieldName")]
    public string? ObjectIdFieldName { get; init; }

    [JsonPropertyName("objectIds")]
    public long[]? ObjectIds { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesRestError? Error { get; init; }
}

internal sealed record GeoServicesRestLayerResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("objectIdField")]
    public string? ObjectIdField { get; init; }

    [JsonPropertyName("maxRecordCount")]
    public int? MaxRecordCount { get; init; }

    [JsonPropertyName("extent")]
    public GeoServicesRestExtent? Extent { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesRestError? Error { get; init; }
}

internal sealed record GeoServicesRestError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

#pragma warning restore CA1812

/// <summary>
/// Source-generated JSON serialization context for ArcGIS REST API responses.
/// AOT/trim-safe so the federated provider can run under Native AOT.
/// </summary>
[JsonSerializable(typeof(GeoServicesRestQueryResponse))]
[JsonSerializable(typeof(GeoServicesRestCountResponse))]
[JsonSerializable(typeof(GeoServicesRestExtentResponse))]
[JsonSerializable(typeof(GeoServicesRestObjectIdsResponse))]
[JsonSerializable(typeof(GeoServicesRestLayerResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GeoServicesRestJsonContext : JsonSerializerContext
{
}
