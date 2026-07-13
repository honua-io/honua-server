// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// Resolves a published <c>(serviceId, layerId)</c> tuple to the canonical
/// Metadata v2 publication/resource and the integer storage-layer handle that
/// <see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader"/>
/// and <see cref="Honua.Core.Features.Raster.Abstractions.IRasterMapRenderer"/>
/// consume. The same snapshot extensions back the GeoServices FeatureServer and
/// MapServer adapters, so the MCP tools resolve layers identically rather than
/// growing their own catalog lookup.
/// </summary>
internal readonly record struct MapToolLayerContext(
    MetadataV2Service Service,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource,
    int StorageLayerId);

internal static class MapToolLayerResolver
{
    /// <summary>
    /// Resolves the layer addressing tuple against the supplied metadata
    /// snapshot. The service is matched by id first, then by name (mirroring the
    /// GeoServices catalog which accepts either). Failures surface as
    /// <see cref="GeoprocessingNotFoundException"/> /
    /// <see cref="GeoprocessingValidationException"/> so they flow through the
    /// shared MCP error mapper.
    /// </summary>
    public static MapToolLayerContext Resolve(
        MetadataV2GraphSnapshot snapshot,
        string? serviceId,
        int? layerId)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            throw new GeoprocessingValidationException("'serviceId' is required.");
        }

        if (layerId is not { } resolvedLayerId || resolvedLayerId < 0)
        {
            throw new GeoprocessingValidationException("'layerId' is required and must be a non-negative integer.");
        }

        var service = ResolveService(snapshot, serviceId.Trim());
        if (service is null)
        {
            throw new GeoprocessingNotFoundException($"Service '{serviceId}' was not found in the published catalog.");
        }

        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, resolvedLayerId);
        if (publication is null)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedLayerId} was not found on service '{service.Metadata.Name}'.");
        }

        var resource = snapshot.ResolveResource(publication);
        if (resource is null)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedLayerId} on service '{service.Metadata.Name}' has no backing resource.");
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource);
        if (storageLayerId is not { } resolvedStorageLayerId)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedLayerId} on service '{service.Metadata.Name}' is not backed by queryable storage.");
        }

        return new MapToolLayerContext(service, publication, resource, resolvedStorageLayerId);
    }

    private static MetadataV2Service? ResolveService(MetadataV2GraphSnapshot snapshot, string serviceId)
        => snapshot.Graph.Services.FirstOrDefault(candidate =>
               string.Equals(candidate.Metadata.Id, serviceId, StringComparison.OrdinalIgnoreCase))
           ?? snapshot.FindService(serviceId);
}
