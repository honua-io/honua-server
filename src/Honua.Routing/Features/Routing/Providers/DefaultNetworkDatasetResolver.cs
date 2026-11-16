// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

internal sealed class DefaultNetworkDatasetResolver : INetworkDatasetResolver
{
    public Task<NetworkDataset?> ResolveAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        return Task.FromResult<NetworkDataset?>(
            string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase)
                ? NetworkDataset.Default
                : null);
    }
}
