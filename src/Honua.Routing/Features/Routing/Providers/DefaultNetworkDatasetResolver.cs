// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Trivial network-dataset resolver that only knows the built-in
/// <see cref="NetworkDataset.Default"/> (the existing <c>public.ways</c> topology).
/// Used by the simple <see cref="PgRoutingProvider"/> constructor and direct
/// construction in tests that do not exercise the registry. Production DI registers
/// the Postgres-backed <see cref="NetworkDatasetRegistry"/> instead.
/// </summary>
internal sealed class DefaultNetworkDatasetResolver : INetworkDatasetResolver
{
    /// <inheritdoc />
    public Task<NetworkDataset?> ResolveAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        return Task.FromResult<NetworkDataset?>(
            string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase)
                ? NetworkDataset.Default
                : null);
    }
}
