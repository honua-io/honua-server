// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.DataEnrichment.Domain;

/// <summary>
/// Configuration-driven catalog of enrichment datasets (#374). Each entry names a
/// registered reference layer (an administrative boundary, POI, or demographic
/// layer already published to the catalog) so callers can enrich their own
/// features by a stable <c>datasetKey</c> instead of having to know the internal
/// layer id of the reference data.
/// </summary>
/// <remarks>
/// <para>
/// This MVP operates over <em>registered layers only</em>. Bundling or proxying
/// third-party demographic sources (CARTO Data Observatory style ACS / Census /
/// OSM extracts) is intentionally deferred: no such dataset ships with the
/// server, and wiring an external provider is out of scope for the first
/// increment. Operators populate the catalog by publishing the reference data as
/// an ordinary layer and registering its key here. See
/// <c>docs/operator/data-enrichment.md</c>.
/// </para>
/// </remarks>
public sealed class EnrichmentCatalogOptions
{
    /// <summary>
    /// Configuration section bound from <c>DataEnrichment</c>.
    /// </summary>
    public const string SectionName = "DataEnrichment";

    /// <summary>
    /// Registered enrichment datasets keyed by <see cref="EnrichmentDatasetOptions.Key"/>.
    /// Empty by default — the catalog is opt-in and operator-curated.
    /// </summary>
    public IList<EnrichmentDatasetOptions> Datasets { get; set; } = new List<EnrichmentDatasetOptions>();
}

/// <summary>
/// A single registered enrichment dataset: a stable key plus the reference layer
/// and default spatial-join behavior used when callers reference it.
/// </summary>
public sealed class EnrichmentDatasetOptions
{
    /// <summary>
    /// Stable, caller-facing identifier (e.g. <c>admin-boundaries</c>,
    /// <c>poi</c>, <c>census-tracts</c>). Case-insensitive.
    /// </summary>
    [Required]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name surfaced in the catalog listing.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Coarse classification used for catalog presentation only:
    /// <c>boundary</c>, <c>poi</c>, or <c>demographic</c>. Free-form; not enforced.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Identifier of the registered layer that provides the enrichment geometry
    /// and attributes. The caller must have read access to this layer; access is
    /// re-checked on every request through the shared layer-validation pipeline.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int LayerId { get; set; }

    /// <summary>
    /// Default spatial predicate applied when the caller does not specify one:
    /// <c>intersects</c>, <c>contains</c>, <c>within</c>, or <c>dwithin</c>.
    /// Defaults to <c>intersects</c>.
    /// </summary>
    public string Predicate { get; set; } = "intersects";

    /// <summary>
    /// Default <c>dwithin</c> distance in meters used when the predicate is
    /// <c>dwithin</c> and the caller does not override it.
    /// </summary>
    public double? DistanceMeters { get; set; }

    /// <summary>
    /// Default reference-layer attributes carried onto each enriched feature when
    /// the caller does not request a specific subset. Empty means "match count
    /// only" unless the caller supplies <c>attributes</c> or <c>statistics</c>.
    /// </summary>
    public IList<string> Attributes { get; set; } = new List<string>();
}
