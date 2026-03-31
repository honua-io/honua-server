// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Catalog CRUD for registered cloud-hosted COGs.
/// </summary>
public interface ICloudCogStore
{
    /// <summary>
    /// Gets a cloud COG registration by ID.
    /// </summary>
    Task<CloudCogRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new cloud-hosted COG.
    /// </summary>
    Task<CloudCogRegistration> RegisterAsync(CloudCogRegistrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cloud COG registration.
    /// </summary>
    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all cloud COG registrations for a layer.
    /// </summary>
    Task<CloudCogRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the cached metadata for a registration after a metadata scan.
    /// </summary>
    Task UpdateMetadataAsync(long id, CogMetadata metadata, byte[]? ifdCache, CancellationToken cancellationToken = default);
}
