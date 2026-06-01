// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Server-owned, editable open-data page state for a Console content item. This
/// is the authoritative metadata Console edits and previews, and the source the
/// DCAT/data.json, STAC, and Schema.org Dataset projections are generated from.
/// </summary>
/// <remarks>
/// The page is intentionally separate from <see cref="ConsoleContentItem"/>
/// (which owns identity, membership visibility, and lineage) and from
/// <see cref="ConsoleShareState"/> (which owns the share access tier). Publishing
/// an item as open data requires the item to be open-data <em>eligible</em>
/// (a distributable type that is public-indexed) and a page to exist; the page
/// itself never changes the item's access tier.
/// </remarks>
public sealed record ConsoleOpenDataPage
{
    /// <summary>Content item id the page describes.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Open-data dataset title. Falls back to the item title for previews when unset.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Long-form dataset description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Publishing organization name (DCAT <c>publisher.name</c>).</summary>
    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    /// <summary>Point-of-contact full name (DCAT <c>contactPoint.fn</c>).</summary>
    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    /// <summary>Point-of-contact email (DCAT <c>contactPoint.hasEmail</c>).</summary>
    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    /// <summary>License URL or SPDX identifier (DCAT <c>license</c>).</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Public landing page URL for the dataset.</summary>
    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    /// <summary>Discovery keywords (DCAT <c>keyword</c> / STAC keywords).</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Distribution/service access links offered for the dataset.</summary>
    [JsonPropertyName("distributions")]
    public IReadOnlyList<ConsoleOpenDataDistribution> Distributions { get; init; } = Array.Empty<ConsoleOpenDataDistribution>();

    /// <summary>Spatial coverage, when known.</summary>
    [JsonPropertyName("spatialExtent")]
    public ConsoleSpatialExtent? SpatialExtent { get; init; }

    /// <summary>Temporal coverage, when known.</summary>
    [JsonPropertyName("temporalExtent")]
    public ConsoleTemporalExtent? TemporalExtent { get; init; }

    /// <summary>Free-form provenance references shown on the page (lineage notes/URLs).</summary>
    [JsonPropertyName("provenanceRefs")]
    public IReadOnlyList<string> ProvenanceRefs { get; init; } = Array.Empty<string>();

    /// <summary>Timestamp the page was last updated, when state exists.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Principal that last updated the page (audit), when state exists.</summary>
    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

/// <summary>
/// A single distribution (access link) offered for an open-data dataset. Maps to
/// a DCAT <c>distribution</c> and a STAC asset.
/// </summary>
public sealed record ConsoleOpenDataDistribution
{
    /// <summary>Human-readable distribution title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Access URL (download URL or service endpoint).</summary>
    [JsonPropertyName("accessUrl")]
    public required string AccessUrl { get; init; }

    /// <summary>IANA media type / DCAT <c>mediaType</c> (e.g. <c>application/geo+json</c>).</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    /// <summary>Short format label (e.g. <c>GeoJSON</c>, <c>OGC API - Features</c>).</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }
}

/// <summary>
/// Axis-aligned geographic bounding box in WGS84 longitude/latitude degrees.
/// </summary>
public sealed record ConsoleSpatialExtent
{
    /// <summary>Western-most longitude.</summary>
    [JsonPropertyName("west")]
    public required double West { get; init; }

    /// <summary>Southern-most latitude.</summary>
    [JsonPropertyName("south")]
    public required double South { get; init; }

    /// <summary>Eastern-most longitude.</summary>
    [JsonPropertyName("east")]
    public required double East { get; init; }

    /// <summary>Northern-most latitude.</summary>
    [JsonPropertyName("north")]
    public required double North { get; init; }
}

/// <summary>
/// Temporal coverage. Either bound may be null for an open-ended interval.
/// </summary>
public sealed record ConsoleTemporalExtent
{
    /// <summary>Start of the interval (inclusive), or null for unbounded start.</summary>
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the interval (inclusive), or null for an ongoing/unbounded end.</summary>
    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; init; }
}
