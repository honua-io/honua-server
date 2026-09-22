// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Api.Maps.Handlers;

/// <summary>
/// Shared collision-aware resolution of an OGC API - Maps collection to its canonical
/// resource and Maps-enabled service.
/// </summary>
/// <remarks>
/// A compatibility publication can expose the same numeric layer id through both a raster
/// binding and a vector/map binding. <see cref="MetadataV2GraphIndex.ResourcesByStorageLayerId"/>
/// is deliberately one-to-one and first-wins, so its entry can be the ImageServer-only
/// resource while a later colliding binding owns the valid Maps publication. Every OGC API -
/// Maps surface (rendering + tile-set handlers) resolves through this helper so the
/// map, map/tiles, and dataset-map endpoints agree on the same resource for a colliding
/// storage layer id, and a fallback is logged once so the collision is diagnosable.
/// </remarks>
internal static class OgcMapsResourceResolver
{
    private const string OgcApiMapsProtocol = "OGC-API-Maps";

    /// <summary>
    /// Enumerates the dataset's collision-aware storage-layer entries once, so dataset
    /// rendering and dataset discovery describe the same resources (#5048).
    /// </summary>
    /// <remarks>
    /// Protocol enablement and access policy are deliberately NOT applied here: the caller
    /// applies them, because discovery and rendering evaluate them against their own
    /// operation. Falls back to the indexed resource for non-colliding / non-Maps layers so
    /// their behaviour is unchanged (#2799).
    /// </remarks>
    public static List<(int LayerId, MetadataV2Resource Resource, MetadataV2Service? Service)> EnumerateDatasetEntries(
        MetadataV2GraphSnapshot snapshot,
        ILogger logger)
    {
        var entries = new List<(int LayerId, MetadataV2Resource Resource, MetadataV2Service? Service)>();
        var seenStorageLayerIds = new HashSet<int>();
        foreach (var binding in snapshot.Graph.StorageBindings)
        {
            if (binding.StorageLayerId is not int storageLayerId ||
                !seenStorageLayerIds.Add(storageLayerId))
            {
                continue;
            }

            var (resolvedResource, resolvedService) = ResolveMapsResource(snapshot, storageLayerId, logger);
            if (resolvedResource is not null)
            {
                entries.Add((storageLayerId, resolvedResource, resolvedService));
            }
            else if (snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out var indexResource))
            {
                entries.Add((storageLayerId, indexResource, ResolveOgcApiMapsService(snapshot, indexResource)));
            }
        }

        return entries;
    }

    /// <summary>
    /// Combines the supplied resources into one dataset extent expressed in the first
    /// resource's CRS, using the canonical coordinate transform service (#5048).
    /// </summary>
    /// <remarks>
    /// Rendering previously combined extents through the synchronous
    /// <c>CoordinateTransformer</c>, which supports far fewer CRS pairs than the canonical
    /// PostGIS-backed <see cref="ICoordinateTransformService"/>, and it turned an
    /// unsupported pair into an exception that surfaced as HTTP 500. The transform result is
    /// reported instead: <c>TransformUnavailable</c> lets the caller answer with an
    /// actionable client error, or omit the metadata, rather than inventing an extent.
    /// </remarks>
    /// <returns>
    /// The combined extent (<see langword="null"/> when no resource declares bounds) and
    /// whether a required transform could not be performed.
    /// </returns>
    public static async Task<(FeatureExtent? Extent, bool TransformUnavailable)> BuildDatasetExtentAsync(
        IEnumerable<MetadataV2Resource> resources,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        FeatureExtent? combined = null;
        foreach (var resource in resources)
        {
            var bbox = resource.ReadBbox();
            if (bbox is null)
            {
                continue;
            }

            var srid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
            var extent = FeatureExtent.Create(bbox.West, bbox.South, bbox.East, bbox.North, srid);

            if (combined is not { } current)
            {
                combined = extent;
                continue;
            }

            if (current.SpatialReference != extent.SpatialReference)
            {
                if (coordinateTransformService is null)
                {
                    return (null, true);
                }

                var transformed = await coordinateTransformService.TransformExtentAsync(
                    extent.MinX,
                    extent.MinY,
                    extent.MaxX,
                    extent.MaxY,
                    extent.SpatialReference,
                    current.SpatialReference,
                    cancellationToken).ConfigureAwait(false);
                if (transformed is not { } bounds)
                {
                    return (null, true);
                }

                extent = FeatureExtent.Create(
                    bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, current.SpatialReference);
            }

            combined = FeatureExtent.Create(
                Math.Min(current.MinX, extent.MinX),
                Math.Min(current.MinY, extent.MinY),
                Math.Max(current.MaxX, extent.MaxX),
                Math.Max(current.MaxY, extent.MaxY),
                current.SpatialReference);
        }

        return (combined, false);
    }

    /// <summary>
    /// Resolves the resource and Maps-enabled service for a storage layer id, preferring the
    /// O(1) indexed entry and only falling back to a colliding binding when the indexed
    /// resource is not published through a Maps-enabled service.
    /// </summary>
    public static (MetadataV2Resource? Resource, MetadataV2Service? Service) ResolveMapsResource(
        MetadataV2GraphSnapshot snapshot,
        int storageLayerId,
        ILogger logger)
    {
        MetadataV2Resource? indexedResource = null;
        if (snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out indexedResource))
        {
            var indexedService = ResolveOgcApiMapsService(snapshot, indexedResource);
            if (indexedService is not null)
            {
                return (indexedResource, indexedService);
            }
        }

        foreach (var binding in snapshot.Graph.StorageBindings)
        {
            if (binding.StorageLayerId != storageLayerId ||
                !snapshot.Index.ResourcesById.TryGetValue(binding.ResourceId, out var resource))
            {
                continue;
            }

            var service = ResolveOgcApiMapsService(snapshot, resource);
            if (service is null)
            {
                continue;
            }

            if (indexedResource is not null &&
                !string.Equals(indexedResource.Metadata.Id, resource.Metadata.Id, StringComparison.Ordinal))
            {
                OgcMapsLog.StorageLayerCollisionFallback(
                    logger,
                    storageLayerId,
                    indexedResource.Metadata.Id,
                    resource.Metadata.Id);
            }

            return (resource, service);
        }

        return (null, null);
    }

    /// <summary>
    /// Returns the first Maps-enabled service that publishes <paramref name="resource"/>, or
    /// <see langword="null"/> when no Maps publication exists.
    /// </summary>
    public static MetadataV2Service? ResolveOgcApiMapsService(MetadataV2GraphSnapshot snapshot, MetadataV2Resource resource)
    {
        foreach (var publication in snapshot.Index.PublicationsByResource[resource.Metadata.Id])
        {
            if (!snapshot.IsRoutable(publication))
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var candidate))
            {
                continue;
            }
            if (IsProtocolEnabled(candidate, OgcApiMapsProtocol))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Determines whether <paramref name="service"/> advertises the supplied protocol.
    /// </summary>
    public static bool IsProtocolEnabled(MetadataV2Service? service, string protocol)
        => service?.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase)) == true;
}
