// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Catalog CRUD for registered cloud-hosted COGs.
/// </summary>
public interface ICogStore
{
    /// <summary>
    /// Gets a COG registration by ID.
    /// </summary>
    Task<CogRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new cloud-hosted COG.
    /// </summary>
    Task<CogRegistration> RegisterAsync(CogRegistrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a COG registration.
    /// </summary>
    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all COG registrations for a layer.
    /// </summary>
    Task<CogRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the cached metadata for a registration after a metadata scan.
    /// </summary>
    Task UpdateMetadataAsync(long id, CogMetadata metadata, byte[]? ifdCache, CancellationToken cancellationToken = default);
}
