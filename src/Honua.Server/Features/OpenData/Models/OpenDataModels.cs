// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.OpenData.Domain;

namespace Honua.Server.Features.OpenData.Models;

/// <summary>
/// Request body for updating server-owned open-data page metadata.
/// </summary>
public sealed class OpenDataPageUpdateRequest
{
    /// <summary>Public title override.</summary>
    [StringLength(500)]
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Public description override.</summary>
    [StringLength(4000)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Publisher metadata.</summary>
    [JsonPropertyName("publisher")]
    public OpenDataOrganization? Publisher { get; init; }

    /// <summary>Contact-point metadata.</summary>
    [JsonPropertyName("contactPoint")]
    public OpenDataContact? ContactPoint { get; init; }

    /// <summary>License URI or short identifier.</summary>
    [StringLength(1000)]
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Discovery tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Landing page URL.</summary>
    [StringLength(2000)]
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Distribution and service links.</summary>
    [JsonPropertyName("distributions")]
    public IReadOnlyList<OpenDataDistribution>? Distributions { get; init; }

    /// <summary>WGS84 spatial coverage.</summary>
    [JsonPropertyName("spatialCoverage")]
    public OpenDataSpatialExtent? SpatialCoverage { get; init; }

    /// <summary>Temporal coverage.</summary>
    [JsonPropertyName("temporalCoverage")]
    public OpenDataTemporalExtent? TemporalCoverage { get; init; }

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenanceReferences")]
    public IReadOnlyList<ConsoleProvenanceRef>? ProvenanceReferences { get; init; }

    /// <summary>True when anonymous publication is requested.</summary>
    [JsonPropertyName("isPublished")]
    public bool? IsPublished { get; init; }
}

/// <summary>
/// Admin response for open-data page state and eligibility.
/// </summary>
public sealed class OpenDataPageAdminResponse
{
    /// <summary>Item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Eligibility result.</summary>
    [JsonPropertyName("eligibility")]
    public required OpenDataEligibility Eligibility { get; init; }

    /// <summary>Open-data page record.</summary>
    [JsonPropertyName("page")]
    public required OpenDataPageRecord Page { get; init; }
}

/// <summary>
/// Anonymous open-data page projection.
/// </summary>
public sealed class OpenDataItemResponse
{
    /// <summary>Item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Item type.</summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>Open-data page title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Open-data page description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Publisher metadata.</summary>
    [JsonPropertyName("publisher")]
    public OpenDataOrganization? Publisher { get; init; }

    /// <summary>Contact-point metadata.</summary>
    [JsonPropertyName("contactPoint")]
    public OpenDataContact? ContactPoint { get; init; }

    /// <summary>License URI or short identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Discovery tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Landing page URL.</summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Distribution and service links.</summary>
    [JsonPropertyName("distributions")]
    public IReadOnlyList<OpenDataDistribution> Distributions { get; init; } = Array.Empty<OpenDataDistribution>();

    /// <summary>WGS84 spatial coverage.</summary>
    [JsonPropertyName("spatialCoverage")]
    public OpenDataSpatialExtent? SpatialCoverage { get; init; }

    /// <summary>Temporal coverage.</summary>
    [JsonPropertyName("temporalCoverage")]
    public OpenDataTemporalExtent? TemporalCoverage { get; init; }

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenanceReferences")]
    public IReadOnlyList<ConsoleProvenanceRef> ProvenanceReferences { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>Schema.org Dataset JSON-LD preview projection.</summary>
    [JsonPropertyName("schemaOrg")]
    public required SchemaOrgDatasetResponse SchemaOrg { get; init; }

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Anonymous open-data list response.
/// </summary>
public sealed class OpenDataListResponse
{
    /// <summary>Items in the current page.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<OpenDataItemResponse> Items { get; init; }

    /// <summary>Total visible items.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary>Cursor for the next page.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Schema.org Dataset JSON-LD projection used by Console page previews.
/// </summary>
public sealed class SchemaOrgDatasetResponse
{
    /// <summary>JSON-LD context.</summary>
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://schema.org";

    /// <summary>JSON-LD type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Dataset";

    /// <summary>Dataset name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Dataset URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Dataset license.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Dataset keywords.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>Dataset publisher.</summary>
    [JsonPropertyName("publisher")]
    public OpenDataOrganization? Publisher { get; init; }

    /// <summary>Dataset distributions.</summary>
    [JsonPropertyName("distribution")]
    public IReadOnlyList<SchemaOrgDistributionResponse> Distribution { get; init; } = Array.Empty<SchemaOrgDistributionResponse>();
}

/// <summary>
/// Schema.org DataDownload projection for one distribution.
/// </summary>
public sealed class SchemaOrgDistributionResponse
{
    /// <summary>JSON-LD type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "DataDownload";

    /// <summary>Distribution name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Distribution content URL.</summary>
    [JsonPropertyName("contentUrl")]
    public string? ContentUrl { get; init; }

    /// <summary>Distribution encoding format.</summary>
    [JsonPropertyName("encodingFormat")]
    public string? EncodingFormat { get; init; }
}

/// <summary>
/// Public DCAT/data.json catalog response.
/// </summary>
public sealed class DcatCatalogResponse
{
    /// <summary>JSON-LD context.</summary>
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://resources.data.gov/schemas/dcat-us/v3.0/schema/catalog.jsonld";

    /// <summary>JSON-LD type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Catalog";

    /// <summary>Catalog title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = "Honua Open Data Catalog";

    /// <summary>Catalog description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = "Open-data datasets published by this Honua deployment.";

    /// <summary>Catalog dataset entries.</summary>
    [JsonPropertyName("dataset")]
    public required IReadOnlyList<DcatDatasetResponse> Dataset { get; init; }

    /// <summary>Generation timestamp.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// DCAT dataset entry.
/// </summary>
public sealed class DcatDatasetResponse
{
    /// <summary>JSON-LD type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Dataset";

    /// <summary>Stable dataset identifier.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>Dataset title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Publisher metadata.</summary>
    [JsonPropertyName("publisher")]
    public OpenDataOrganization? Publisher { get; init; }

    /// <summary>Contact-point metadata.</summary>
    [JsonPropertyName("contactPoint")]
    public OpenDataContact? ContactPoint { get; init; }

    /// <summary>License URI or short identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Keywords.</summary>
    [JsonPropertyName("keyword")]
    public IReadOnlyList<string> Keyword { get; init; } = Array.Empty<string>();

    /// <summary>Landing page URL.</summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Last modified timestamp.</summary>
    [JsonPropertyName("modified")]
    public DateTimeOffset Modified { get; init; }

    /// <summary>Spatial coverage as GeoJSON.</summary>
    [JsonPropertyName("spatial")]
    public DcatSpatialResponse? Spatial { get; init; }

    /// <summary>Temporal coverage.</summary>
    [JsonPropertyName("temporal")]
    public OpenDataTemporalExtent? Temporal { get; init; }

    /// <summary>Distribution links.</summary>
    [JsonPropertyName("distribution")]
    public IReadOnlyList<DcatDistributionResponse> Distribution { get; init; } = Array.Empty<DcatDistributionResponse>();
}

/// <summary>
/// GeoJSON Polygon spatial extent for DCAT.
/// </summary>
public sealed class DcatSpatialResponse
{
    /// <summary>GeoJSON type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Polygon";

    /// <summary>GeoJSON coordinates in WGS84 longitude/latitude order.</summary>
    [JsonPropertyName("coordinates")]
    public required double[][][][] Coordinates { get; init; }
}

/// <summary>
/// DCAT distribution entry.
/// </summary>
public sealed class DcatDistributionResponse
{
    /// <summary>JSON-LD type.</summary>
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "dcat:Distribution";

    /// <summary>Distribution title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Distribution description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Media type.</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    /// <summary>Format label.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Access URL.</summary>
    [JsonPropertyName("accessURL")]
    public string? AccessUrl { get; init; }

    /// <summary>Download URL.</summary>
    [JsonPropertyName("downloadURL")]
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// Admin DCAT status response.
/// </summary>
public sealed class OpenDataDcatStatusResponse
{
    /// <summary>Public catalog URL.</summary>
    [JsonPropertyName("catalogUrl")]
    public required string CatalogUrl { get; init; }

    /// <summary>Validation summary.</summary>
    [JsonPropertyName("validation")]
    public required OpenDataValidationSummary Validation { get; init; }

    /// <summary>Timestamp when status was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Request body for DCAT validation preview.
/// </summary>
public sealed class OpenDataDcatValidateRequest
{
    /// <summary>Optional item filter.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }
}

/// <summary>
/// Request body for publishing a Console item to the STAC control surface.
/// </summary>
public sealed class StacPublicationPublishRequest
{
    /// <summary>Source Console content item identifier.</summary>
    [Required]
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Optional STAC collection identifier.</summary>
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; init; }

    /// <summary>Optional title override.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional description override.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// Request body for updating STAC publication metadata.
/// </summary>
public sealed class StacPublicationUpdateRequest
{
    /// <summary>Optional title override.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional description override.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// Admin STAC publication readback response.
/// </summary>
public sealed class StacPublicationStatusResponse
{
    /// <summary>STAC collection identifier.</summary>
    [JsonPropertyName("collectionId")]
    public required string CollectionId { get; init; }

    /// <summary>Source Console content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Publication status.</summary>
    [JsonPropertyName("status")]
    public required OpenDataStacPublicationStatus Status { get; init; }

    /// <summary>Public STAC collection URL.</summary>
    [JsonPropertyName("publicStacCollectionUrl")]
    public required string PublicStacCollectionUrl { get; init; }

    /// <summary>Title override or source title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Description override or source description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Eligibility readback at the time of the operation.</summary>
    [JsonPropertyName("eligibility")]
    public OpenDataEligibility? Eligibility { get; init; }

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}
