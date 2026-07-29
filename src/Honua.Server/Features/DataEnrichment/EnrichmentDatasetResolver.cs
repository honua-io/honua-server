// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Adapts the composing <see cref="EnrichmentDatasetCatalogService"/> onto the
/// neutral <see cref="IEnrichmentDatasetResolver"/> abstraction (#2283) so the
/// <c>enrichment.enrich</c> geoprocessing job executor resolves datasets through
/// the same merged managed/configuration catalog as the synchronous
/// <c>POST /api/enrich</c> endpoint, without depending on this server slice.
/// </summary>
internal sealed class EnrichmentDatasetResolver : IEnrichmentDatasetResolver
{
    private readonly EnrichmentDatasetCatalogService _catalog;

    /// <summary>Initializes a new instance of the <see cref="EnrichmentDatasetResolver"/> class.</summary>
    /// <param name="catalog">The composing enrichment-dataset catalog service.</param>
    public EnrichmentDatasetResolver(EnrichmentDatasetCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public async Task<EnrichmentDatasetDefinition?> ResolveAsync(
        string idOrKey,
        CancellationToken cancellationToken)
    {
        var resolved = await _catalog.ResolveAsync(idOrKey, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        return new EnrichmentDatasetDefinition(
            resolved.Id,
            resolved.Title,
            resolved.Category,
            resolved.LayerId,
            resolved.DefaultPredicate,
            resolved.DistanceMeters,
            resolved.Attributes,
            resolved.Attribution,
            resolved.MinimumEdition,
            resolved.Source);
    }
}
