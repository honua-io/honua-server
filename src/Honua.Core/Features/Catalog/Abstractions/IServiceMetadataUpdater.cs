// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Catalog.Abstractions;

/// <summary>
/// Abstraction for updating service metadata in the catalog store.
/// </summary>
public interface IServiceMetadataUpdater
{
    /// <summary>
    /// Updates the metadata JSONB column for a service.
    /// </summary>
    /// <param name="serviceName">Service name (case-insensitive)</param>
    /// <param name="metadata">New metadata value</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateServiceMetadataAsync(string serviceName, CatalogMetadata metadata, CancellationToken cancellationToken = default);
}
