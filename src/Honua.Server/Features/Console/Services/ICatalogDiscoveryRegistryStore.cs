// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

// This is a read-only projection, and implementations are not expected to write.
public interface ICatalogDiscoveryRegistryStore
{
    Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default);

    Task<CatalogEndpointDetail?> GetEndpointAsync(string workspaceId, string endpointKey, CancellationToken cancellationToken = default);

    Task<CatalogItem?> GetItemAsync(string workspaceId, string endpointKey, string itemId, CancellationToken cancellationToken = default);
}
