// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IZarrStore
{
    Task<ZarrRegistration?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<ZarrRegistration> RegisterAsync(ZarrRegistrationRequest request, CancellationToken cancellationToken = default);

    Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default);

    Task<ZarrRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(long id, ZarrStoreMetadata metadata, CancellationToken cancellationToken = default);
}
