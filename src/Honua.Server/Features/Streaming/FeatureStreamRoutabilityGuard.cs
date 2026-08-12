// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Holds the latest metadata-derived service/layer routing set for already-open stream filters.
/// Updating the immutable state changes matching for every filter that references this singleton,
/// so retiring a publication does not require reconnecting each transport first.
/// </summary>
internal sealed class FeatureStreamRoutabilityGuard
{
    private RoutabilityState _state = RoutabilityState.Empty;

    public void Update(MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in snapshot.Graph.Services)
        {
            foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
            {
                if (!snapshot.IsRoutable(publication))
                {
                    continue;
                }

                var resource = snapshot.ResolveResource(publication);
                var layerId = snapshot.ResolveStorageLayerId(publication)
                    ?? (resource is null ? null : snapshot.ResolveStorageLayerId(resource))
                    ?? publication.LayerIndex;
                if (!layerId.HasValue)
                {
                    continue;
                }

                routes.Add(RouteKey(service.Metadata.Id, layerId.Value));
                routes.Add(RouteKey(service.Metadata.Name, layerId.Value));
            }
        }

        Volatile.Write(ref _state, new RoutabilityState(snapshot.Graph.Revision, routes));
    }

    public void Invalidate() => Volatile.Write(ref _state, RoutabilityState.Empty);

    public bool IsRoutable(string serviceId, int layerId)
        => Volatile.Read(ref _state).Routes.Contains(RouteKey(serviceId, layerId));

    private static string RouteKey(string serviceId, int layerId)
        => string.Concat(serviceId, "\u001f", layerId.ToString(CultureInfo.InvariantCulture));

    private sealed record RoutabilityState(long Revision, HashSet<string> Routes)
    {
        public static RoutabilityState Empty { get; } = new(
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
