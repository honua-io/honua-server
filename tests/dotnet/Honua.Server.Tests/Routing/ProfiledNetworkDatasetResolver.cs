// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Server.Tests.Routing;

internal sealed class ProfiledNetworkDatasetResolver : INetworkDatasetResolver
{
    private static readonly NetworkDataset Dataset = NetworkDataset.Default with
    {
        TravelProfiles =
        [
            RoutingTravelProfile.Driving,
            new RoutingTravelProfile("walking", "walking_cost", "walking_reverse_cost"),
        ],
    };

    public Task<NetworkDataset?> ResolveAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<NetworkDataset?>(
            string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase)
                ? Dataset
                : null);
    }
}
