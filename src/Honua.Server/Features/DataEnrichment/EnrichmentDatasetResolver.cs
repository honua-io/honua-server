// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Server.Features.DataEnrichment;

internal sealed class EnrichmentDatasetResolver : IEnrichmentDatasetResolver
{
    private readonly EnrichmentDatasetCatalogService _catalog;

    public EnrichmentDatasetResolver(EnrichmentDatasetCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

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
