// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.MapTools;

internal readonly record struct MapToolLayerContext(
    MetadataV2Service Service,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource,
    int StorageLayerId);

internal static class MapToolLayerResolver
{
    public static Task<IFeatureReader> ResolveReaderAsync(
        HttpContext httpContext,
        MetadataV2GraphSnapshot snapshot,
        MapToolLayerContext layer,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken)
    {
        // Match the REST adapter's provider selection for bound publications.
        var binding = snapshot.ResolveStorageBinding(layer.Publication);
        var router = httpContext.RequestServices.GetService<FeatureProviderQueryRouter>();
        if (binding is not null && router is not null)
        {
            return router.ResolveReaderAsync(snapshot, layer.Service, layer.Resource,
                layer.Publication, layer.StorageLayerId, operation, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(binding?.ConnectionId))
        {
            throw new GeoprocessingStoreUnavailableException("The connection-bound feature query runtime is not available on this server.");
        }

        // Managed/storeless compositions may provide only the default reader.
        return Task.FromResult(httpContext.RequestServices.GetRequiredService<IFeatureReader>());
    }

    private static MapToolLayerContext Resolve(
        MetadataV2GraphSnapshot snapshot,
        string? serviceId,
        int? serviceLayerIndex)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            throw new GeoprocessingValidationException("'serviceId' is required.");
        }

        if (serviceLayerIndex is not { } resolvedServiceLayerIndex || resolvedServiceLayerIndex < 0)
        {
            throw new GeoprocessingValidationException("'serviceLayerIndex' is required and must be a non-negative integer.");
        }

        var service = ResolveService(snapshot, serviceId.Trim());
        if (service is null)
        {
            throw new GeoprocessingNotFoundException($"Service '{serviceId}' was not found in the published catalog.");
        }

        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, resolvedServiceLayerIndex);
        if (publication is null)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedServiceLayerIndex} was not found on service '{service.Metadata.Name}'.");
        }

        var resource = snapshot.ResolveResource(publication);
        if (!snapshot.IsRoutable(publication))
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedServiceLayerIndex} on service '{service.Metadata.Name}' has no backing resource.");
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource!);
        if (storageLayerId is not { } resolvedStorageLayerId)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {resolvedServiceLayerIndex} on service '{service.Metadata.Name}' is not backed by queryable storage.");
        }

        return new MapToolLayerContext(service, publication, resource!, resolvedStorageLayerId);
    }

    public static async Task<MapToolLayerContext> ResolveForReadAsync(
        HttpContext httpContext,
        MetadataV2GraphSnapshot snapshot,
        string? serviceId,
        int? serviceLayerIndex,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        var layer = Resolve(snapshot, serviceId, serviceLayerIndex);
        await EnsureAccessAsync(httpContext, layer, operation, cancellationToken).ConfigureAwait(false);
        return layer;
    }

    public static async Task EnsureAccessAsync(
        HttpContext httpContext,
        MapToolLayerContext layer,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TenantScopeHelpers.IsPublicationVisible(httpContext, layer.Publication, layer.Resource, layer.Service))
        {
            throw new GeoprocessingNotFoundException("Layer was not found in the published catalog.");
        }

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            httpContext, layer.Resource, layer.Service, operation, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new GeoprocessingAuthorizationException(decision.RequiresAuthentication);
        }
    }

    private static MetadataV2Service? ResolveService(MetadataV2GraphSnapshot snapshot, string serviceId)
        => snapshot.Graph.Services.FirstOrDefault(candidate =>
               string.Equals(candidate.Metadata.Id, serviceId, StringComparison.OrdinalIgnoreCase))
           ?? snapshot.FindService(serviceId);
}
