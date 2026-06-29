// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Domain;

/// <summary>
/// A managed enrichment-dataset catalog entry (#2280). Designates an existing
/// managed layer as a reusable enrichment source (administrative boundary,
/// demographic, or POI reference data) so enrichment callers can reference it by a
/// stable slug instead of a bare numeric layer id, and so provenance, attribution,
/// license, and the minimum edition tier travel with the dataset.
/// </summary>
/// <param name="Id">
/// Stable, caller-facing slug (lowercase, e.g. <c>ne-admin-0-countries</c>). Used as
/// the catalog primary key and the <c>datasetId</c> resolved by <c>POST /api/enrich</c>.
/// </param>
/// <param name="Title">Human-readable display name surfaced in discovery responses.</param>
/// <param name="Category">
/// Coarse classification: <see cref="EnrichmentDatasetCategories.Boundary"/>,
/// <see cref="EnrichmentDatasetCategories.Demographic"/>, or
/// <see cref="EnrichmentDatasetCategories.Poi"/>.
/// </param>
/// <param name="LayerId">Identifier of the backing managed layer/collection.</param>
/// <param name="GeometryType">
/// Optional declared geometry type of the backing layer (e.g. <c>Polygon</c>,
/// <c>Point</c>); descriptive metadata only.
/// </param>
/// <param name="JoinAttributes">
/// Default reference-layer attributes (the joinable/key fields) carried onto each
/// enriched feature when the caller does not request a specific subset.
/// </param>
/// <param name="DefaultPredicate">
/// Default spatial predicate applied when the caller specifies neither a
/// <c>method</c> nor a <c>predicate</c>: <c>intersects</c>, <c>contains</c>,
/// <c>within</c>, or <c>dwithin</c>.
/// </param>
/// <param name="DistanceMeters">
/// Default <c>dwithin</c> distance in meters used when the effective predicate is
/// <c>dwithin</c> and the caller does not override it.
/// </param>
/// <param name="Provenance">Free-form provenance/source description (e.g. dataset version, URL).</param>
/// <param name="Attribution">
/// Attribution string downstream consumers must surface to comply with the data
/// provider's terms (echoed in enrichment responses).
/// </param>
/// <param name="License">License identifier or description (e.g. <c>Public Domain (Natural Earth)</c>).</param>
/// <param name="MinimumEdition">
/// Minimum edition tier required to discover and enrich against this dataset.
/// Community-tier datasets are visible to all editions; Pro/Enterprise datasets are
/// filtered out for lower editions.
/// </param>
/// <param name="CreatedAt">Registration timestamp.</param>
/// <param name="UpdatedAt">Last-update timestamp.</param>
/// <param name="CreatedBy">Admin identity that registered the dataset.</param>
/// <param name="UpdatedBy">Admin identity that last updated the dataset.</param>
public sealed record EnrichmentDatasetRecord(
    string Id,
    string Title,
    string Category,
    int LayerId,
    string? GeometryType,
    IReadOnlyList<string> JoinAttributes,
    string DefaultPredicate,
    double? DistanceMeters,
    string? Provenance,
    string? Attribution,
    string? License,
    HonuaEdition MinimumEdition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy);

/// <summary>
/// Well-known enrichment-dataset category values (#2280). Categories are a coarse
/// classification used for discovery presentation; the set is closed for the
/// boundary/demographic/POI taxonomy the enrichment epic (#374) defines.
/// </summary>
public static class EnrichmentDatasetCategories
{
    /// <summary>Administrative or other boundary reference data (polygons).</summary>
    public const string Boundary = "boundary";

    /// <summary>Demographic reference data (census tracts, statistical areas).</summary>
    public const string Demographic = "demographic";

    /// <summary>Points of interest reference data.</summary>
    public const string Poi = "poi";

    /// <summary>
    /// Returns whether <paramref name="value"/> is one of the recognised categories
    /// (case-insensitive).
    /// </summary>
    /// <param name="value">Candidate category value.</param>
    /// <returns><c>true</c> when the value is a recognised category.</returns>
    public static bool IsValid(string? value) =>
        string.Equals(value, Boundary, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Demographic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Poi, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Thrown when registering an enrichment dataset whose id already exists in the
/// catalog (#2280).
/// </summary>
public sealed class EnrichmentDatasetAlreadyExistsException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnrichmentDatasetAlreadyExistsException"/> class.
    /// </summary>
    /// <param name="datasetId">The conflicting dataset id.</param>
    public EnrichmentDatasetAlreadyExistsException(string datasetId)
        : base($"An enrichment dataset with id '{datasetId}' is already registered.")
        => DatasetId = datasetId;

    /// <summary>Gets the conflicting dataset id.</summary>
    public string DatasetId { get; }
}
