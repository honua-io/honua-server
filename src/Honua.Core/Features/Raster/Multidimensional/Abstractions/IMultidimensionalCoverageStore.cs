// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

/// <summary>
/// Catalog CRUD for cloud-hosted multidimensional coverage sources
/// (HDF5 / NetCDF4).
/// </summary>
public interface IMultidimensionalCoverageStore
{
    /// <summary>
    /// Gets a multidimensional coverage registration by identifier.
    /// </summary>
    Task<MultidimensionalCoverageRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new cloud-hosted multidimensional coverage source.
    /// </summary>
    Task<MultidimensionalCoverageRegistration> RegisterAsync(
        MultidimensionalCoverageRegistrationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a multidimensional coverage registration.
    /// </summary>
    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists multidimensional coverage registrations for a layer.
    /// </summary>
    Task<MultidimensionalCoverageRegistration[]> ListByLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates cached metadata for a registration after a metadata scan.
    /// </summary>
    Task UpdateMetadataAsync(
        long id,
        MultidimensionalCoverageMetadata metadata,
        CancellationToken cancellationToken = default);
}
