// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

public interface IMultidimensionalCoverageStore
{
    Task<MultidimensionalCoverageRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<MultidimensionalCoverageRegistration> RegisterAsync(
        MultidimensionalCoverageRegistrationRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    Task<MultidimensionalCoverageRegistration[]> ListByLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(
        long id,
        MultidimensionalCoverageMetadata metadata,
        CancellationToken cancellationToken = default);
}
