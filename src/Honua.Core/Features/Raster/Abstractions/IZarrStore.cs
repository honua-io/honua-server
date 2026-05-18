// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Catalog CRUD for registered Zarr stores. Persistence backend is provider-specific;
/// the default in-memory implementation is sufficient for the MVP read-only flow.
/// </summary>
public interface IZarrStore
{
    /// <summary>
    /// Gets a Zarr registration by ID.
    /// </summary>
    Task<ZarrRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new Zarr store.
    /// </summary>
    Task<ZarrRegistration> RegisterAsync(ZarrRegistrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a Zarr registration.
    /// </summary>
    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all Zarr registrations for a layer.
    /// </summary>
    Task<ZarrRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the cached metadata for a registration after a metadata scan.
    /// </summary>
    Task UpdateMetadataAsync(long id, ZarrStoreMetadata metadata, CancellationToken cancellationToken = default);
}
