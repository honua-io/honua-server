// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;

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
}
