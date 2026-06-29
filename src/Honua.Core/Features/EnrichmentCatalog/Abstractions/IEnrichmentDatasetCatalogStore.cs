// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Abstractions;

/// <summary>
/// Persistence abstraction for the managed enrichment-dataset catalog (#2280).
/// Implemented by the active data provider (PostGIS) over a dedicated registry
/// table. Registration/deregistration/update are admin operations; discovery and
/// the enrichment compute path read through this store.
/// </summary>
public interface IEnrichmentDatasetCatalogStore
{
    /// <summary>
    /// Lists all registered enrichment datasets ordered by id.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The registered enrichment datasets.</returns>
    Task<IReadOnlyList<EnrichmentDatasetRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a single enrichment dataset by its id.
    /// </summary>
    /// <param name="id">Dataset id (slug).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dataset, or <c>null</c> when no dataset is registered with that id.</returns>
    Task<EnrichmentDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new enrichment dataset.
    /// </summary>
    /// <param name="dataset">The dataset to register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted dataset (with server-assigned timestamps).</returns>
    /// <exception cref="EnrichmentDatasetAlreadyExistsException">
    /// Thrown when a dataset with the same id already exists.
    /// </exception>
    Task<EnrichmentDatasetRecord> RegisterAsync(EnrichmentDatasetRecord dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the mutable fields of an existing enrichment dataset.
    /// </summary>
    /// <param name="dataset">The dataset with updated values (id identifies the row).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted dataset, or <c>null</c> when no dataset with that id exists.</returns>
    Task<EnrichmentDatasetRecord?> UpdateAsync(EnrichmentDatasetRecord dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deregisters (deletes) an enrichment dataset by id.
    /// </summary>
    /// <param name="id">Dataset id (slug).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a dataset was deleted; <c>false</c> when none matched.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
