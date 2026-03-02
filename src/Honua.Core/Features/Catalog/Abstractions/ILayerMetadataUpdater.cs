// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Catalog.Abstractions;

/// <summary>
/// Abstraction for updating layer metadata in the catalog store.
/// </summary>
public interface ILayerMetadataUpdater
{
    /// <summary>
    /// Updates the metadata JSONB column for a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="metadata">New metadata value</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateLayerMetadataAsync(int layerId, CatalogMetadata metadata, CancellationToken cancellationToken = default);
}
