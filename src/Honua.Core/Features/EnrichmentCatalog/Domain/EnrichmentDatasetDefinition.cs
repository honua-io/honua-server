// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Domain;

/// <summary>
/// An enrichment dataset resolved for a compute path (#2283): the backing managed
/// layer plus the effective default join behavior and the gating/attribution
/// metadata the consumer must enforce and echo. Neutral projection of the merged
/// managed/configuration catalog so non-HTTP consumers (the geoprocessing job
/// runtime) can resolve datasets without depending on the server's catalog slice.
/// </summary>
/// <param name="Id">Resolved dataset id (managed slug) or configuration key.</param>
/// <param name="Title">Human-readable display name, when available.</param>
/// <param name="Category">Coarse classification (boundary, demographic, poi).</param>
/// <param name="LayerId">Identifier of the backing managed layer.</param>
/// <param name="DefaultPredicate">
/// Default spatial predicate applied when the caller specifies neither a method nor
/// a predicate: <c>intersects</c>, <c>contains</c>, <c>within</c>, or <c>dwithin</c>.
/// </param>
/// <param name="DistanceMeters">
/// Default <c>dwithin</c> distance in meters, when the dataset declares one.
/// </param>
/// <param name="Attributes">Default carried attributes when the caller requests no subset.</param>
/// <param name="Attribution">
/// Attribution string downstream consumers must surface to comply with the data
/// provider's terms; echoed on enrichment outputs.
/// </param>
/// <param name="MinimumEdition">Minimum edition tier required to enrich against this dataset.</param>
/// <param name="Source">Origin of the entry: <c>managed</c> or <c>config</c>.</param>
public sealed record EnrichmentDatasetDefinition(
    string Id,
    string? Title,
    string Category,
    int LayerId,
    string DefaultPredicate,
    double? DistanceMeters,
    IReadOnlyList<string> Attributes,
    string? Attribution,
    HonuaEdition MinimumEdition,
    string Source);
