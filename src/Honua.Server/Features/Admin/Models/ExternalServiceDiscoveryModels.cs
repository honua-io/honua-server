// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

internal sealed record ExternalServiceDiscoveryRequest
{
    public string? Url { get; init; }

    public string? ServiceUrl { get; init; }

    public int? TimeoutSeconds { get; init; }

    public ExternalServiceCredentials? Credentials { get; init; }
}

// Discovery credentials are used for that request only and are never stored.
internal sealed record ExternalServiceCredentials
{
    public string? Mode { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? Token { get; init; }

    public string? TokenUrl { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? Referer { get; init; }
}

internal sealed record ExternalServiceDiscoveryResponse
{
    public required string SourceUrl { get; init; }

    public required string NormalizedUrl { get; init; }

    public required string SourceKind { get; init; }

    public required string ServiceType { get; init; }

    public required string ServiceName { get; init; }

    public string? Description { get; init; }

    public int? Srid { get; init; }

    public ExternalServiceLayerCandidate[] Candidates { get; init; } = [];

    public ExternalServiceSummary[] Services { get; init; } = [];

    public bool IsCatalog { get; init; }

    public string[] Warnings { get; init; } = [];
}

internal sealed record ExternalServiceSummary
{
    public required string SourceKind { get; init; }

    public required string ServiceName { get; init; }

    public required string ServiceType { get; init; }

    public required string ServiceUrl { get; init; }

    public string? FolderPath { get; init; }

    public int? Srid { get; init; }

    public ExternalServiceLayerCandidate[] Candidates { get; init; } = [];
}

internal sealed record ExternalServiceLayerCandidate
{
    public required string SourceKind { get; init; }

    public required string ServiceName { get; init; }

    public required string ServiceUrl { get; init; }

    public string? ExternalId { get; init; }

    public int? LayerId { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? LayerType { get; init; }

    public string? GeometryType { get; init; }

    public int? Srid { get; init; }

    public ExternalServiceExtent? Extent { get; init; }

    public ExternalServiceField[] Fields { get; init; } = [];

    public int? FeatureCount { get; init; }
}

internal sealed record ExternalServiceExtent
{
    public double XMin { get; init; }

    public double YMin { get; init; }

    public double XMax { get; init; }

    public double YMax { get; init; }

    public int? Srid { get; init; }
}

internal sealed record ExternalServiceField
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Alias { get; init; }

    public int? Length { get; init; }

    public bool? Nullable { get; init; }
}

internal sealed record GeoServicesServiceDocument
{
    [JsonPropertyName("serviceDescription")]
    public string? ServiceDescription { get; init; }

    [JsonPropertyName("mapName")]
    public string? MapName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("spatialReference")]
    public GeoServicesSpatialReferenceDocument? SpatialReference { get; init; }

    [JsonPropertyName("layers")]
    public GeoServicesLayerReferenceDocument[]? Layers { get; init; }

    [JsonPropertyName("tables")]
    public GeoServicesLayerReferenceDocument[]? Tables { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesErrorDocument? Error { get; init; }
}

internal sealed record GeoServicesCatalogDocument
{
    [JsonPropertyName("folders")]
    public string[]? Folders { get; init; }

    [JsonPropertyName("services")]
    public GeoServicesCatalogServiceDocument[]? Services { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesErrorDocument? Error { get; init; }
}

internal sealed record GeoServicesCatalogServiceDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed record GeoServicesTokenDocument
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expires")]
    public long? Expires { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesErrorDocument? Error { get; init; }
}

internal sealed record OAuthTokenDocument
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

internal sealed record GeoServicesLayerReferenceDocument
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record GeoServicesLayerDocument
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("extent")]
    public GeoServicesExtentDocument? Extent { get; init; }

    [JsonPropertyName("fields")]
    public GeoServicesFieldDocument[]? Fields { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("featureCount")]
    public int? FeatureCount { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesErrorDocument? Error { get; init; }
}

internal sealed record GeoServicesCountDocument
{
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("error")]
    public GeoServicesErrorDocument? Error { get; init; }
}

internal sealed record GeoServicesExtentDocument
{
    [JsonPropertyName("xmin")]
    public double XMin { get; init; }

    [JsonPropertyName("ymin")]
    public double YMin { get; init; }

    [JsonPropertyName("xmax")]
    public double XMax { get; init; }

    [JsonPropertyName("ymax")]
    public double YMax { get; init; }

    [JsonPropertyName("spatialReference")]
    public GeoServicesSpatialReferenceDocument? SpatialReference { get; init; }
}

internal sealed record GeoServicesSpatialReferenceDocument
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record GeoServicesFieldDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("length")]
    public int? Length { get; init; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }
}

internal sealed record GeoServicesErrorDocument
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }
}

internal sealed record OgcLandingDocument
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("links")]
    public OgcLinkDocument[]? Links { get; init; }
}

internal sealed record OgcCollectionsDocument
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("collections")]
    public OgcCollectionDocument[]? Collections { get; init; }
}

internal sealed record OgcCollectionDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("itemType")]
    public string? ItemType { get; init; }

    [JsonPropertyName("extent")]
    public OgcExtentDocument? Extent { get; init; }

    [JsonPropertyName("crs")]
    public string[]? Crs { get; init; }

    [JsonPropertyName("storageCrs")]
    public string? StorageCrs { get; init; }

    [JsonPropertyName("itemCount")]
    public int? ItemCount { get; init; }

    [JsonPropertyName("links")]
    public OgcLinkDocument[]? Links { get; init; }
}

internal sealed record OgcExtentDocument
{
    [JsonPropertyName("spatial")]
    public OgcSpatialExtentDocument? Spatial { get; init; }
}

internal sealed record OgcSpatialExtentDocument
{
    [JsonPropertyName("bbox")]
    public double[][]? Bbox { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }
}

internal sealed record OgcLinkDocument
{
    [JsonPropertyName("href")]
    public string? Href { get; init; }

    [JsonPropertyName("rel")]
    public string? Rel { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}
