// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;
using MobileFeature = Honua.Mobile.Core.Models.Feature;
using MobileEditResult = Honua.Mobile.Core.Models.EditResult;

namespace Honua.Mobile.Core.Abstractions;

/// <summary>
/// Provides write access to geospatial features using gRPC protocols.
/// </summary>
public interface IFeatureWriter
{
    /// <summary>
    /// Applies a batch of feature edits (adds, updates, deletes) to a layer.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="edits">Batch of feature edits to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results of the edit operations</returns>
    Task<MobileEditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEditBatch edits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates new features in the layer.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="features">Features to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results of the create operations</returns>
    Task<MobileEditResult> CreateFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<MobileFeature> features,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates existing features in the layer.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="features">Features to update (must include IDs)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results of the update operations</returns>
    Task<MobileEditResult> UpdateFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<MobileFeature> features,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes features from the layer by object IDs.
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="objectIds">Object IDs of features to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results of the delete operations</returns>
    Task<MobileEditResult> DeleteFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<long> objectIds,
        CancellationToken cancellationToken = default);
}