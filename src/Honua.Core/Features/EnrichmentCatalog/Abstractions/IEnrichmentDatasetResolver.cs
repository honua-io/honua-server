// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Abstractions;

/// <summary>
/// Resolves enrichment datasets from the merged managed/configuration catalog for
/// compute paths outside the HTTP enrichment slice (#2283) — most notably the
/// <c>enrichment.enrich</c> geoprocessing job executor. Implemented by the server's
/// composing catalog service; deployments without the enrichment slice simply do
/// not register an implementation, and consumers fail closed.
/// </summary>
public interface IEnrichmentDatasetResolver
{
    /// <summary>
    /// Resolves a dataset by managed id first, then by configuration key. Edition
    /// filtering is NOT applied here; the consumer enforces the dataset's
    /// <see cref="EnrichmentDatasetDefinition.MinimumEdition"/> gate.
    /// </summary>
    /// <param name="idOrKey">Managed dataset id or configuration key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved dataset, or <see langword="null"/> when no dataset matches.</returns>
    Task<EnrichmentDatasetDefinition?> ResolveAsync(string idOrKey, CancellationToken cancellationToken);
}
