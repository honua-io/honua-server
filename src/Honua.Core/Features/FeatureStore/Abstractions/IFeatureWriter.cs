// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides write access to features with create, update, and delete operations.
/// Segregated from read operations to support different authorization and caching policies.
/// </summary>
public interface IFeatureWriter
{
    /// <summary>
    /// Creates a new feature
    /// </summary>
    /// <param name="layerId">Layer identifier where the feature should be created</param>
    /// <param name="feature">Feature to create (Id may be ignored and auto-generated)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created feature with assigned ID</returns>
    Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing feature
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="feature">Feature with updated values (must include valid Id)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated feature</returns>
    /// <exception cref="ResourceNotFoundException">Thrown if feature with the specified ID does not exist</exception>
    Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a feature by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Unique feature identifier to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if feature was deleted, false if not found</returns>
    Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a batch of create, update, and delete operations in a single transaction
    /// </summary>
    /// <param name="layerId">Layer identifier for the operations</param>
    /// <param name="editBatch">Batch of operations to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result summary of the batch operation</returns>
    Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 convenience overload of <see cref="ApplyEditsAsync(int, FeatureEditBatch, CancellationToken)"/>
    /// that accepts a <see cref="MetadataV2Resource"/> instead of the integer
    /// storage-layer handle. Resolves the storage-layer id via
    /// <see cref="IMetadataV2GraphProvider"/> and dispatches to the int-keyed
    /// implementation. Default-interface method so existing implementations
    /// require no changes.
    /// </summary>
    /// <param name="graphProvider">Metadata v2 graph provider used to resolve
    /// the storage-layer id from the current snapshot.</param>
    /// <param name="resource">Canonical V2 resource whose primary storage
    /// binding identifies the target layer.</param>
    /// <param name="editBatch">Batch of operations to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the resource has
    /// no resolvable storage-layer id (e.g. document-only resources).</exception>
    async Task<FeatureEditResult> ApplyEditsAsync(
        IMetadataV2GraphProvider graphProvider,
        MetadataV2Resource resource,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphProvider);
        ArgumentNullException.ThrowIfNull(resource);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = snapshot.ResolveStorageLayerId(resource)
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Metadata.Id}' has no resolvable storage-layer id.");
        return await ApplyEditsAsync(storageLayerId, editBatch, cancellationToken).ConfigureAwait(false);
    }
}
