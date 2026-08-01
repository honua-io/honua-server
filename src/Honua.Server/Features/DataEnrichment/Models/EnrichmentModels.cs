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
/// The endpoint is a thin adapter over the shared spatial-join pipeline (#2282):
/// the <c>method</c> vocabulary maps to the canonical spatial predicates, and the
/// dataset is resolved through the managed enrichment catalog (#2280). Inline
/// GeoJSON source feature sets and the nearest-neighbor method are deferred to the
/// async/managed batch path; see <c>docs/operator/data-enrichment.md</c>.
/// </remarks>
internal sealed class EnrichmentRequest
{
    /// <summary>
    /// Identifier of the managed enrichment dataset (slug) to resolve through the
    /// catalog (#2280/#2282). Preferred over <see cref="DatasetKey"/>.
    /// </summary>
    [JsonPropertyName("datasetId")]
    public string? DatasetId { get; set; }

    /// <summary>
    /// Back-compat alias for the configuration-driven enrichment dataset key. Used
    /// only when <see cref="DatasetId"/> is not supplied.
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
    /// Enrichment method vocabulary (#2282) mapping to the canonical spatial
    /// predicates. The vocabulary is dataset-subject (#3069) — it describes what the
    /// enrichment dataset does to the source feature being enriched:
    /// <c>intersects</c>, <c>point-in-polygon</c>/<c>pip</c>/<c>contains</c> (the
    /// dataset polygon contains the source feature), <c>within</c> (the dataset
    /// feature sits inside the source feature), <c>within-distance</c> (→ dwithin).
    /// <c>nearest-neighbor</c> is recognised but requires the async batch path. Takes
    /// precedence over <see cref="Predicate"/>.
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>
    /// Optional spatial predicate override (intersects, contains, within, dwithin),
    /// read with the same dataset-subject direction as <see cref="Method"/> (#3069):
    /// <c>contains</c> means the dataset feature contains the source feature and
    /// <c>within</c> means the dataset feature is inside the source feature. Falls
    /// back to the dataset default when neither <see cref="Method"/> nor this is
    /// supplied.
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
    /// Enrichment-layer attributes carried onto each source feature (#2282). Falls
    /// back to <see cref="Attributes"/> then the dataset default when omitted.
    /// </summary>
    [JsonPropertyName("outputFields")]
    public string[]? OutputFields { get; set; }

    /// <summary>
    /// Back-compat alias for <see cref="OutputFields"/>.
    /// </summary>
    [JsonPropertyName("attributes")]
    public string[]? Attributes { get; set; }

    /// <summary>
    /// Optional per-match aggregates (count/sum/avg/min/max) computed over the
    /// matched enrichment-layer features (#2282).
    /// </summary>
    [JsonPropertyName("aggregates")]
    public EnrichmentAggregate[]? Aggregates { get; set; }

    /// <summary>
    /// Inline GeoJSON FeatureCollection source (#2282). Deferred to the async/managed
    /// path in this slice; supplying it returns a clear error directing callers to the
    /// layer-backed source or the async batch enrichment job.
    /// </summary>
    [JsonPropertyName("features")]
    public System.Text.Json.JsonElement? Features { get; set; }
}

/// <summary>
/// A single per-match aggregate over the matched enrichment-layer features (#2282):
/// count/sum/avg/min/max applied to one reference-layer attribute.
/// </summary>
internal sealed class EnrichmentAggregate
{
    /// <summary>Aggregate type: count, sum, avg, min, or max.</summary>
    [JsonPropertyName("statisticType")]
    public string? StatisticType { get; set; }

    /// <summary>Reference-layer attribute the aggregate is computed over (omit for count).</summary>
    [JsonPropertyName("onField")]
    public string? OnField { get; set; }

    /// <summary>Optional output property name; defaults to <c>{statisticType}_{onField}</c>.</summary>
    [JsonPropertyName("outName")]
    public string? OutName { get; set; }
}
