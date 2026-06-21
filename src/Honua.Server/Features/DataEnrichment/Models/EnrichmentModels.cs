// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

/// <summary>
/// Catalog listing returned by <c>GET /api/enrich/catalog</c> (#374). Enumerates
/// the registered enrichment datasets the caller may reference by key.
/// </summary>
internal sealed class EnrichmentCatalogResponse
{
    /// <summary>
    /// Registered enrichment datasets visible to the caller.
    /// </summary>
    [JsonPropertyName("datasets")]
    public EnrichmentDatasetDescriptor[] Datasets { get; set; } = [];
}

/// <summary>
/// Public descriptor for a single registered enrichment dataset. Internal layer
/// ids and default join behavior are surfaced so SDK callers can introspect the
/// catalog before enriching.
/// </summary>
internal sealed class EnrichmentDatasetDescriptor
{
    /// <summary>Stable caller-facing key.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Coarse classification (boundary, poi, demographic).</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Default spatial predicate applied when none is supplied.</summary>
    [JsonPropertyName("defaultPredicate")]
    public string DefaultPredicate { get; set; } = "intersects";

    /// <summary>Default carried attributes, when configured.</summary>
    [JsonPropertyName("attributes")]
    public string[] Attributes { get; set; } = [];
}

/// <summary>
/// Request body for <c>POST /api/enrich</c>. The caller names a source layer to
/// enrich and the registered enrichment dataset to draw attributes from, plus
/// optional overrides for the spatial predicate and carried attributes.
/// </summary>
/// <remarks>
/// Inline GeoJSON source feature sets are intentionally deferred for this first
/// increment: enrichment operates over a registered source layer only, reusing
/// the canonical spatial-join pipeline. The deferral is documented in
/// <c>docs/operator/data-enrichment.md</c>.
/// </remarks>
internal sealed class EnrichmentRequest
{
    /// <summary>
    /// Identifier of the registered enrichment dataset (see the catalog endpoint).
    /// </summary>
    [JsonPropertyName("datasetKey")]
    public string? DatasetKey { get; set; }

    /// <summary>
    /// Identifier of the registered source layer whose features are enriched.
    /// </summary>
    [JsonPropertyName("sourceLayerId")]
    public int? SourceLayerId { get; set; }

    /// <summary>
    /// Optional ArcGIS-style SQL filter restricting the source features.
    /// </summary>
    [JsonPropertyName("where")]
    public string? Where { get; set; }

    /// <summary>
    /// Optional spatial predicate override (intersects, contains, within, dwithin).
    /// Falls back to the dataset default when omitted.
    /// </summary>
    [JsonPropertyName("predicate")]
    public string? Predicate { get; set; }

    /// <summary>
    /// Optional dwithin distance in meters; required when the effective predicate
    /// is dwithin and the dataset declares no default distance.
    /// </summary>
    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; set; }

    /// <summary>
    /// Optional override of the enrichment-layer attributes carried onto each
    /// source feature. Falls back to the dataset default when omitted.
    /// </summary>
    [JsonPropertyName("attributes")]
    public string[]? Attributes { get; set; }
}
