// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request to discover externally hosted service/layer candidates.
/// </summary>
internal sealed record ExternalServiceDiscoveryRequest
{
    /// <summary>
    /// URL to the external service root.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Compatibility alias for import-oriented callers that already send serviceUrl.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Optional per-request timeout in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Optional credentials for authenticated discovery of a protected service or catalog.
    /// </summary>
    public ExternalServiceCredentials? Credentials { get; init; }
}

/// <summary>
/// Credentials used to authenticate against a protected external service or catalog. Secrets are used
/// transiently for the discovery request only and are never persisted by the discovery service.
/// </summary>
internal sealed record ExternalServiceCredentials
{
    /// <summary>
    /// Authentication mode: <c>arcgis-token</c> (exchange username/password for an ArcGIS token),
    /// <c>token</c> (use a supplied ArcGIS token/API key directly), <c>basic</c> (HTTP Basic), or
    /// <c>oauth</c> (client-credentials grant against a token endpoint).
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Username for <c>arcgis-token</c> and <c>basic</c> modes.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Password for <c>arcgis-token</c> and <c>basic</c> modes.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Pre-issued token / API key for <c>token</c> mode.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Token endpoint: the ArcGIS portal generateToken URL for <c>arcgis-token</c> mode, or the OAuth token
    /// endpoint for <c>oauth</c> mode. When omitted for <c>arcgis-token</c> it is derived from the service host.
    /// </summary>
    public string? TokenUrl { get; init; }

    /// <summary>
    /// OAuth client id for <c>oauth</c> mode.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth client secret for <c>oauth</c> mode.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Optional referer sent when minting an ArcGIS token (some portals bind tokens to a referer).
    /// </summary>
    public string? Referer { get; init; }
}

/// <summary>
/// Response containing discovered external service candidates.
/// </summary>
internal sealed record ExternalServiceDiscoveryResponse
{
    /// <summary>
    /// Original URL supplied by the caller.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Normalized service-root URL used for discovery.
    /// </summary>
    public required string NormalizedUrl { get; init; }

    /// <summary>
    /// Stable source kind for downstream import planning.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// External service type such as FeatureServer or MapServer.
    /// </summary>
    public required string ServiceType { get; init; }

    /// <summary>
    /// Display name reported by the service, or derived from the URL.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Optional service description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Service-level SRID if the service reports one.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Discovered layer/table candidates flattened across every service (the full selectable set).
    /// </summary>
    public ExternalServiceLayerCandidate[] Candidates { get; init; } = [];

    /// <summary>
    /// Discovered services. For a single-service URL this contains one entry; for a catalog root or folder it
    /// contains every enumerated service grouped by folder. Each service carries its own candidate layers.
    /// </summary>
    public ExternalServiceSummary[] Services { get; init; } = [];

    /// <summary>
    /// True when the supplied URL was an ArcGIS catalog root or folder that was enumerated into multiple services.
    /// </summary>
    public bool IsCatalog { get; init; }

    /// <summary>
    /// Non-fatal discovery warnings.
    /// </summary>
    public string[] Warnings { get; init; } = [];
}

/// <summary>
/// A single external service discovered within a catalog (or the sole service for a single-service URL).
/// </summary>
internal sealed record ExternalServiceSummary
{
    /// <summary>
    /// Stable source kind for the service.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Service display name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// External service type such as FeatureServer or MapServer.
    /// </summary>
    public required string ServiceType { get; init; }

    /// <summary>
    /// Service root URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Catalog folder the service belongs to, or null when at the catalog root / for a single service.
    /// </summary>
    public string? FolderPath { get; init; }

    /// <summary>
    /// Service-level SRID if reported.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Layer/table candidates belonging to this service.
    /// </summary>
    public ExternalServiceLayerCandidate[] Candidates { get; init; } = [];
}

/// <summary>
/// Discovered layer/table candidate from an external service.
/// </summary>
internal sealed record ExternalServiceLayerCandidate
{
    /// <summary>
    /// Source kind inherited from the external service.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// External service display name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// External service root URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Stable source identifier for the candidate when the source does not expose a numeric layer id.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// Numeric layer id when the source exposes one.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Layer or table name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Source display title when it differs from the stable source name.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Layer or table description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// ArcGIS layer type such as Feature Layer or Table.
    /// </summary>
    public string? LayerType { get; init; }

    /// <summary>
    /// Geometry type if available.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Candidate SRID if available.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Candidate extent if available.
    /// </summary>
    public ExternalServiceExtent? Extent { get; init; }

    /// <summary>
    /// Attribute fields reported by the source.
    /// </summary>
    public ExternalServiceField[] Fields { get; init; } = [];

    /// <summary>
    /// Feature count if the source exposes it cheaply.
    /// </summary>
    public int? FeatureCount { get; init; }
}

/// <summary>
/// External service extent.
/// </summary>
internal sealed record ExternalServiceExtent
{
    public double XMin { get; init; }

    public double YMin { get; init; }

    public double XMax { get; init; }

    public double YMax { get; init; }

    public int? Srid { get; init; }
}

/// <summary>
/// External service field metadata.
/// </summary>
internal sealed record ExternalServiceField
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Alias { get; init; }

    public int? Length { get; init; }

    public bool? Nullable { get; init; }
}

internal sealed record ArcGisServiceDocument
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
    public ArcGisSpatialReferenceDocument? SpatialReference { get; init; }

    [JsonPropertyName("layers")]
    public ArcGisLayerReferenceDocument[]? Layers { get; init; }

    [JsonPropertyName("tables")]
    public ArcGisLayerReferenceDocument[]? Tables { get; init; }

    [JsonPropertyName("error")]
    public ArcGisErrorDocument? Error { get; init; }
}

internal sealed record ArcGisCatalogDocument
{
    [JsonPropertyName("folders")]
    public string[]? Folders { get; init; }

    [JsonPropertyName("services")]
    public ArcGisCatalogServiceDocument[]? Services { get; init; }

    [JsonPropertyName("error")]
    public ArcGisErrorDocument? Error { get; init; }
}

internal sealed record ArcGisCatalogServiceDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed record ArcGisTokenDocument
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expires")]
    public long? Expires { get; init; }

    [JsonPropertyName("error")]
    public ArcGisErrorDocument? Error { get; init; }
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

internal sealed record ArcGisLayerReferenceDocument
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record ArcGisLayerDocument
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
    public ArcGisExtentDocument? Extent { get; init; }

    [JsonPropertyName("fields")]
    public ArcGisFieldDocument[]? Fields { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("featureCount")]
    public int? FeatureCount { get; init; }

    [JsonPropertyName("error")]
    public ArcGisErrorDocument? Error { get; init; }
}

internal sealed record ArcGisCountDocument
{
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("error")]
    public ArcGisErrorDocument? Error { get; init; }
}

internal sealed record ArcGisExtentDocument
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
    public ArcGisSpatialReferenceDocument? SpatialReference { get; init; }
}

internal sealed record ArcGisSpatialReferenceDocument
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record ArcGisFieldDocument
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

internal sealed record ArcGisErrorDocument
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
