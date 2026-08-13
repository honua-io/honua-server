// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Holds the latest metadata-derived service/layer routing set for already-open stream filters.
/// Updating the immutable state changes matching for every filter that references this singleton,
/// so retiring a publication does not require reconnecting each transport first.
/// </summary>
internal sealed class FeatureStreamRoutabilityGuard
{
    private long _nextRefreshGeneration;
    private RoutabilityState _state = RoutabilityState.Empty;

    public long BeginRefresh() => Interlocked.Increment(ref _nextRefreshGeneration);

    public bool HasValidState => Volatile.Read(ref _state).IsValid;

    public ValueTask<MetadataV2GraphSnapshot> RefreshAsync(
        IMetadataV2GraphProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return RefreshAsync(provider.GetCurrentAsync, cancellationToken);
    }

    public async ValueTask<MetadataV2GraphSnapshot> RefreshAsync(
        Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> readSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);

        var refreshGeneration = BeginRefresh();
        try
        {
            var snapshot = await readSnapshot(cancellationToken).ConfigureAwait(false);
            Update(refreshGeneration, snapshot);
            return snapshot;
        }
        catch
        {
            Invalidate(refreshGeneration);
            throw;
        }
    }

    public void Update(long refreshGeneration, MetadataV2GraphSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refreshGeneration);
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

        var next = new RoutabilityState(refreshGeneration, routes, IsValid: true);
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (next.RefreshGeneration < current.RefreshGeneration)
            {
                return;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, current), current))
            {
                return;
            }
        }
    }

    public void Invalidate(long refreshGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refreshGeneration);

        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (refreshGeneration < current.RefreshGeneration)
            {
                return;
            }

            var invalid = new RoutabilityState(
                refreshGeneration,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                IsValid: false);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, invalid, current), current))
            {
                return;
            }
        }
    }

    public bool IsRoutable(string serviceId, int layerId)
        => Volatile.Read(ref _state).Routes.Contains(RouteKey(serviceId, layerId));

    private static string RouteKey(string serviceId, int layerId)
        => string.Concat(serviceId, "\u001f", layerId.ToString(CultureInfo.InvariantCulture));

    private sealed record RoutabilityState(long RefreshGeneration, HashSet<string> Routes, bool IsValid)
    {
        public static RoutabilityState Empty { get; } = new(
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IsValid: false);
    }
}
