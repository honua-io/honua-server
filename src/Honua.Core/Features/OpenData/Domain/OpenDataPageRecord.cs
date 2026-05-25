// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.OpenData.Domain;

/// <summary>
/// Server-owned open-data page state for a Console content item.
/// </summary>
public sealed record OpenDataPageRecord
{
    /// <summary>
    /// Console content item identifier this page describes.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Public page and catalog title override.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Public page and catalog description override.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Dataset publisher.
    /// </summary>
    [JsonPropertyName("publisher")]
    public OpenDataOrganization? Publisher { get; init; }

    /// <summary>
    /// Dataset contact point.
    /// </summary>
    [JsonPropertyName("contactPoint")]
    public OpenDataContact? ContactPoint { get; init; }

    /// <summary>
    /// License URI or short identifier.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// Search and discovery tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Human-facing landing page URL.
    /// </summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>
    /// Download, service, and distribution links exposed to consumers.
    /// </summary>
    [JsonPropertyName("distributions")]
    public IReadOnlyList<OpenDataDistribution> Distributions { get; init; } = Array.Empty<OpenDataDistribution>();

    /// <summary>
    /// WGS84 spatial coverage for catalog publication.
    /// </summary>
    [JsonPropertyName("spatialCoverage")]
    public OpenDataSpatialExtent? SpatialCoverage { get; init; }

    /// <summary>
    /// Temporal coverage for catalog publication.
    /// </summary>
    [JsonPropertyName("temporalCoverage")]
    public OpenDataTemporalExtent? TemporalCoverage { get; init; }

    /// <summary>
    /// Provenance references that explain how this item was produced.
    /// </summary>
    [JsonPropertyName("provenanceReferences")]
    public IReadOnlyList<ConsoleProvenanceRef> ProvenanceReferences { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>
    /// True when the page is intended for anonymous publication.
    /// </summary>
    [JsonPropertyName("isPublished")]
    public bool IsPublished { get; init; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Principal identifier that last changed the page.
    /// </summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// Organization projected into open-data metadata.
/// </summary>
public sealed record OpenDataOrganization
{
    /// <summary>
    /// Organization display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Organization URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
/// Contact point projected into open-data metadata.
/// </summary>
public sealed record OpenDataContact
{
    /// <summary>
    /// Contact display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Contact email address.
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }
}

/// <summary>
/// One public distribution or service link for an open-data item.
/// </summary>
public sealed record OpenDataDistribution
{
    /// <summary>
    /// Link title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Link description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Media type, such as application/geo+json.
    /// </summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    /// <summary>
    /// Format label, such as GeoJSON or STAC.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>
    /// Direct download URL when available.
    /// </summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Browser or API access URL when available.
    /// </summary>
    [JsonPropertyName("accessUrl")]
    public string? AccessUrl { get; init; }

    /// <summary>
    /// Service URL when the distribution is an API endpoint.
    /// </summary>
    [JsonPropertyName("serviceUrl")]
    public string? ServiceUrl { get; init; }
}

/// <summary>
/// WGS84 bounding box used for open-data, DCAT, and Schema.org projections.
/// </summary>
public sealed record OpenDataSpatialExtent
{
    /// <summary>
    /// Minimum longitude.
    /// </summary>
    [JsonPropertyName("minX")]
    public required double MinX { get; init; }

    /// <summary>
    /// Minimum latitude.
    /// </summary>
    [JsonPropertyName("minY")]
    public required double MinY { get; init; }

    /// <summary>
    /// Maximum longitude.
    /// </summary>
    [JsonPropertyName("maxX")]
    public required double MaxX { get; init; }

    /// <summary>
    /// Maximum latitude.
    /// </summary>
    [JsonPropertyName("maxY")]
    public required double MaxY { get; init; }

    /// <summary>
    /// Coordinate reference system identifier. Only EPSG:4326 is emitted publicly.
    /// </summary>
    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "EPSG:4326";
}

/// <summary>
/// Temporal extent used for open-data catalog projections.
/// </summary>
public sealed record OpenDataTemporalExtent
{
    /// <summary>
    /// Inclusive start timestamp.
    /// </summary>
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    /// <summary>
    /// Inclusive end timestamp.
    /// </summary>
    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; init; }
}

/// <summary>
/// Machine-readable eligibility or validation issue.
/// </summary>
public sealed record OpenDataIssue
{
    /// <summary>
    /// Stable issue code.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable issue message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Optional field path associated with the issue.
    /// </summary>
    [JsonPropertyName("field")]
    public string? Field { get; init; }

    /// <summary>
    /// True when the issue is a documented DCAT exception rather than a publication blocker.
    /// </summary>
    [JsonPropertyName("documentedException")]
    public bool DocumentedException { get; init; }
}

/// <summary>
/// Open-data eligibility result for one content item.
/// </summary>
public sealed record OpenDataEligibility
{
    /// <summary>
    /// Item identifier that was evaluated.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// True when no blocking reasons were found.
    /// </summary>
    [JsonPropertyName("isEligible")]
    public required bool IsEligible { get; init; }

    /// <summary>
    /// Blocking reasons that prevent publication.
    /// </summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<OpenDataIssue> Reasons { get; init; } = Array.Empty<OpenDataIssue>();

    /// <summary>
    /// Non-blocking warnings that may affect catalog quality.
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<OpenDataIssue> Warnings { get; init; } = Array.Empty<OpenDataIssue>();
}

/// <summary>
/// DCAT validation result for one item.
/// </summary>
public sealed record OpenDataValidationResult
{
    /// <summary>
    /// Item identifier that was validated.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// True when no blocking validation issues were found.
    /// </summary>
    [JsonPropertyName("isValid")]
    public required bool IsValid { get; init; }

    /// <summary>
    /// Field-level validation issues.
    /// </summary>
    [JsonPropertyName("issues")]
    public IReadOnlyList<OpenDataIssue> Issues { get; init; } = Array.Empty<OpenDataIssue>();
}

/// <summary>
/// Summary of DCAT validation for the current open-data catalog.
/// </summary>
public sealed record OpenDataValidationSummary
{
    /// <summary>
    /// True when all validated items passed blocking checks.
    /// </summary>
    [JsonPropertyName("isValid")]
    public required bool IsValid { get; init; }

    /// <summary>
    /// Number of items validated.
    /// </summary>
    [JsonPropertyName("itemCount")]
    public required int ItemCount { get; init; }

    /// <summary>
    /// Number of documented validation exceptions.
    /// </summary>
    [JsonPropertyName("validationExceptionCount")]
    public required int ValidationExceptionCount { get; init; }

    /// <summary>
    /// Per-item validation results.
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<OpenDataValidationResult> Items { get; init; } = Array.Empty<OpenDataValidationResult>();
}

/// <summary>
/// STAC publication state tracked for Console controls.
/// </summary>
public sealed record OpenDataStacPublicationRecord
{
    /// <summary>
    /// STAC collection identifier.
    /// </summary>
    [JsonPropertyName("collectionId")]
    public required string CollectionId { get; init; }

    /// <summary>
    /// Source Console content item identifier.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Current publication status.
    /// </summary>
    [JsonPropertyName("status")]
    public required OpenDataStacPublicationStatus Status { get; init; }

    /// <summary>
    /// Public STAC collection URL.
    /// </summary>
    [JsonPropertyName("publicStacCollectionUrl")]
    public required string PublicStacCollectionUrl { get; init; }

    /// <summary>
    /// Optional STAC title override.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>
    /// Optional STAC description override.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Console-managed STAC publication status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenDataStacPublicationStatus>))]
public enum OpenDataStacPublicationStatus
{
    /// <summary>
    /// The item is published into the STAC control surface.
    /// </summary>
    [JsonStringEnumMemberName("published")]
    Published,

    /// <summary>
    /// The item has been unpublished from the STAC control surface.
    /// </summary>
    [JsonStringEnumMemberName("unpublished")]
    Unpublished
}
