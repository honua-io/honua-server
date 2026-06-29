// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

/// <summary>
/// Discovery response for <c>GET /api/enrich/datasets</c> (#2280). Enumerates the
/// managed enrichment datasets visible to the caller after edition filtering.
/// </summary>
internal sealed class EnrichmentDatasetsResponse
{
    /// <summary>Enrichment datasets visible to the caller.</summary>
    [JsonPropertyName("datasets")]
    public EnrichmentDatasetMetadata[] Datasets { get; set; } = [];
}

/// <summary>
/// Public metadata for a single managed enrichment dataset (#2280). Carries the
/// provenance/attribution/license so downstream consumers can comply with the data
/// provider's terms, plus the backing layer reference and default join behavior.
/// </summary>
internal sealed class EnrichmentDatasetMetadata
{
    /// <summary>Stable caller-facing id (slug).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Category: boundary, demographic, or poi.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Identifier of the backing managed layer.</summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    /// <summary>Optional declared geometry type of the backing layer.</summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    /// <summary>Default joinable/key attributes carried onto enriched features.</summary>
    [JsonPropertyName("attributes")]
    public string[] Attributes { get; set; } = [];

    /// <summary>Default spatial predicate applied when none is supplied.</summary>
    [JsonPropertyName("defaultPredicate")]
    public string DefaultPredicate { get; set; } = "intersects";

    /// <summary>Default dwithin distance in meters, when configured.</summary>
    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; set; }

    /// <summary>Free-form provenance/source description.</summary>
    [JsonPropertyName("provenance")]
    public string? Provenance { get; set; }

    /// <summary>Attribution string downstream consumers must surface.</summary>
    [JsonPropertyName("attribution")]
    public string? Attribution { get; set; }

    /// <summary>License identifier or description.</summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    /// <summary>Minimum edition tier required to use this dataset (Community/Pro/Enterprise).</summary>
    [JsonPropertyName("minimumEdition")]
    public string MinimumEdition { get; set; } = "Pro";

    /// <summary>Origin of the entry: <c>managed</c> (registry table) or <c>config</c> (operator config).</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "managed";
}

/// <summary>
/// Request body to register a managed enrichment dataset (#2280). Designates an
/// existing managed layer as a reusable enrichment source.
/// </summary>
public sealed class RegisterEnrichmentDatasetRequest
{
    /// <summary>Stable dataset id (lowercase slug).</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable display name.</summary>
    public string? Title { get; set; }

    /// <summary>Category: boundary, demographic, or poi. Defaults to boundary.</summary>
    public string? Category { get; set; }

    /// <summary>Identifier of the backing managed layer.</summary>
    public int? LayerId { get; set; }

    /// <summary>Optional declared geometry type of the backing layer.</summary>
    public string? GeometryType { get; set; }

    /// <summary>Default joinable/key attributes carried onto enriched features.</summary>
    public string[]? Attributes { get; set; }

    /// <summary>Default spatial predicate (intersects/contains/within/dwithin). Defaults to intersects.</summary>
    public string? DefaultPredicate { get; set; }

    /// <summary>Default dwithin distance in meters.</summary>
    public double? DistanceMeters { get; set; }

    /// <summary>Free-form provenance/source description.</summary>
    public string? Provenance { get; set; }

    /// <summary>Attribution string downstream consumers must surface.</summary>
    public string? Attribution { get; set; }

    /// <summary>License identifier or description.</summary>
    public string? License { get; set; }

    /// <summary>Minimum edition tier (Community/Pro/Enterprise). Defaults to Pro.</summary>
    public string? MinimumEdition { get; set; }
}

/// <summary>
/// Request body to update a managed enrichment dataset (#2280). Omitted fields keep
/// their current value; the id is taken from the route, not the body.
/// </summary>
public sealed class UpdateEnrichmentDatasetRequest
{
    /// <summary>New title, or null to keep the current one.</summary>
    public string? Title { get; set; }

    /// <summary>New category, or null to keep the current one.</summary>
    public string? Category { get; set; }

    /// <summary>New backing layer id, or null to keep the current one.</summary>
    public int? LayerId { get; set; }

    /// <summary>New geometry type, or null to keep the current one.</summary>
    public string? GeometryType { get; set; }

    /// <summary>New default attributes, or null to keep the current ones.</summary>
    public string[]? Attributes { get; set; }

    /// <summary>New default predicate, or null to keep the current one.</summary>
    public string? DefaultPredicate { get; set; }

    /// <summary>New default dwithin distance, or null to keep the current one.</summary>
    public double? DistanceMeters { get; set; }

    /// <summary>New provenance, or null to keep the current one.</summary>
    public string? Provenance { get; set; }

    /// <summary>New attribution, or null to keep the current one.</summary>
    public string? Attribution { get; set; }

    /// <summary>New license, or null to keep the current one.</summary>
    public string? License { get; set; }

    /// <summary>New minimum edition tier, or null to keep the current one.</summary>
    public string? MinimumEdition { get; set; }
}
