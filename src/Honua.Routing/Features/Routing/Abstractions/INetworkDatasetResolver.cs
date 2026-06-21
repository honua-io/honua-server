// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Resolves a network-dataset identifier to its registered
/// <see cref="NetworkDataset"/> (Phase 0 of #1882). The routing provider uses the
/// resolved dataset's edge/vertex table names instead of the previously hardcoded
/// <c>public.ways</c> topology, which makes the network addressable and is the
/// prerequisite for multi-dataset routing and (deferred) dataset editing.
/// </summary>
public interface INetworkDatasetResolver
{
    /// <summary>
    /// Resolves the network dataset with the given identifier.
    /// </summary>
    /// <param name="datasetId">
    /// The dataset identifier (e.g. from
    /// <see cref="RoutingConfiguration.NetworkDatasetId"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved dataset, or <c>null</c> when no dataset with that id is
    /// registered.
    /// </returns>
    Task<NetworkDataset?> ResolveAsync(string datasetId, CancellationToken cancellationToken = default);
}
