// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// Request body for creating or replacing an item's open-data page. All fields
/// are optional editable metadata; the route's <c>{id}</c> binds the item.
/// </summary>
public sealed class UpdateOpenDataPageRequest
{
    /// <summary>Dataset title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Publishing organization name.</summary>
    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    /// <summary>Point-of-contact full name.</summary>
    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    /// <summary>Point-of-contact email.</summary>
    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    /// <summary>License URL or SPDX identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Public landing page URL.</summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Discovery keywords.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Distribution/service access links.</summary>
    [JsonPropertyName("distributions")]
    public IReadOnlyList<ConsoleOpenDataDistribution>? Distributions { get; init; }

    /// <summary>Spatial coverage.</summary>
    [JsonPropertyName("spatialExtent")]
    public ConsoleSpatialExtent? SpatialExtent { get; init; }

    /// <summary>Temporal coverage.</summary>
    [JsonPropertyName("temporalExtent")]
    public ConsoleTemporalExtent? TemporalExtent { get; init; }

    /// <summary>Free-form provenance references.</summary>
    [JsonPropertyName("provenanceRefs")]
    public IReadOnlyList<string>? ProvenanceRefs { get; init; }
}

/// <summary>
/// Why an item is or is not eligible for open-data publication. Server-authored
/// so Console can render the publish controls and the blocking reason verbatim.
/// </summary>
public sealed class ConsoleOpenDataEligibilityResponse
{
    /// <summary>Content item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Item category.</summary>
    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    /// <summary>True when the item may be published as open data.</summary>
    [JsonPropertyName("eligible")]
    public required bool Eligible { get; init; }

    /// <summary>
    /// Stable machine reason code (e.g. <c>not-distributable-type</c>,
    /// <c>not-public-indexed</c>, <c>eligible</c>).
    /// </summary>
    [JsonPropertyName("reasonCode")]
    public required string ReasonCode { get; init; }

    /// <summary>Human-readable explanation of the eligibility decision.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Effective share access tier evaluated for the decision.</summary>
    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    /// <summary>True when an open-data page has been authored for the item.</summary>
    [JsonPropertyName("hasPage")]
    public required bool HasPage { get; init; }
}

/// <summary>
/// Combined open-data page read projection: the editable page, the current
/// eligibility decision, the STAC publication state, and DCAT validation status.
/// </summary>
public sealed class ConsoleOpenDataPageResponse
{
    /// <summary>Editable open-data page fields (page-default filled from the item).</summary>
    [JsonPropertyName("page")]
    public required ConsoleOpenDataPage Page { get; init; }

    /// <summary>Current eligibility decision.</summary>
    [JsonPropertyName("eligibility")]
    public required ConsoleOpenDataEligibilityResponse Eligibility { get; init; }

    /// <summary>STAC publication lifecycle state.</summary>
    [JsonPropertyName("stacPublication")]
    public required ConsoleStacPublicationState StacPublication { get; init; }

    /// <summary>DCAT/data.json validation status for the current page.</summary>
    [JsonPropertyName("dcatValidation")]
    public required ConsoleOpenDataValidationResult DcatValidation { get; init; }
}

/// <summary>
/// DCAT/data.json export preview: the generated catalog document plus its
/// validation status, so Console can show validation success or documented
/// exceptions next to the preview.
/// </summary>
public sealed class ConsoleDcatExportResponse
{
    /// <summary>DCAT-US 3.0 / data.json catalog document.</summary>
    [JsonPropertyName("catalog")]
    public required DcatCatalog Catalog { get; init; }

    /// <summary>Validation status of the dataset the catalog was generated from.</summary>
    [JsonPropertyName("validation")]
    public required ConsoleOpenDataValidationResult Validation { get; init; }
}
