// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.ArcGisRest.Features.FeatureStore.Models;

// JSON wire-format types for ArcGIS REST API responses. Kept intentionally
// minimal — only fields read by the federated read-through provider are present.

#pragma warning disable CA1812 // Internal class is never instantiated (used for JSON deserialization)

internal sealed record ArcGisRestQueryResponse
{
    [JsonPropertyName("features")]
    public ArcGisRestFeature[]? Features { get; init; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }

    [JsonPropertyName("spatialReference")]
    public ArcGisRestSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("objectIdFieldName")]
    public string? ObjectIdFieldName { get; init; }

    [JsonPropertyName("globalIdFieldName")]
    public string? GlobalIdFieldName { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("error")]
    public ArcGisRestError? Error { get; init; }
}

internal sealed record ArcGisRestFeature
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, JsonElement>? Attributes { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }
}

internal sealed record ArcGisRestSpatialReference
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record ArcGisRestCountResponse
{
    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("error")]
    public ArcGisRestError? Error { get; init; }
}

internal sealed record ArcGisRestExtentResponse
{
    [JsonPropertyName("extent")]
    public ArcGisRestExtent? Extent { get; init; }

    [JsonPropertyName("error")]
    public ArcGisRestError? Error { get; init; }
}

internal sealed record ArcGisRestExtent
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
    public ArcGisRestSpatialReference? SpatialReference { get; init; }
}

internal sealed record ArcGisRestObjectIdsResponse
{
    [JsonPropertyName("objectIdFieldName")]
    public string? ObjectIdFieldName { get; init; }

    [JsonPropertyName("objectIds")]
    public long[]? ObjectIds { get; init; }

    [JsonPropertyName("error")]
    public ArcGisRestError? Error { get; init; }
}

internal sealed record ArcGisRestLayerResponse
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
    public ArcGisRestExtent? Extent { get; init; }

    [JsonPropertyName("error")]
    public ArcGisRestError? Error { get; init; }
}

internal sealed record ArcGisRestError
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
[JsonSerializable(typeof(ArcGisRestQueryResponse))]
[JsonSerializable(typeof(ArcGisRestCountResponse))]
[JsonSerializable(typeof(ArcGisRestExtentResponse))]
[JsonSerializable(typeof(ArcGisRestObjectIdsResponse))]
[JsonSerializable(typeof(ArcGisRestLayerResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ArcGisRestJsonContext : JsonSerializerContext
{
}
