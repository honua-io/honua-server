// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Scene.Grpc;

internal static class ElevationAccessGuard
{
    internal readonly record struct AuthorizedElevationLayer(MetadataV2Object Resource, int StorageLayerId);

    private readonly record struct ResolvedElevationLayer
    {
        public required int LayerId { get; init; }

        public required MetadataV2GraphSnapshot Snapshot { get; init; }

        public required MetadataV2Layer Publication { get; init; }

        public required MetadataV2Object Resource { get; init; }

        public required MetadataV2Service? Service { get; init; }
    }

    public static async Task<AuthorizedElevationLayer> EnforceReadAccessAsync(
        ServerCallContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.GetHttpContext();
        if (http is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Elevation access context unavailable."));
        }

        var layer = await ResolveRoutableLayerAsync(http, layerId, cancellationToken).ConfigureAwait(false);
        return await AuthorizeAndResolveStorageLayerAsync(http, layer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResolvedElevationLayer> ResolveRoutableLayerAsync(
        HttpContext http,
        int layerId,
        CancellationToken cancellationToken)
    {
        var provider = http.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var (publication, resource, service) = LayerValidationHelpers.ResolveV2TripleForTenant(
            snapshot,
            layerId,
            ServiceProtocols.Elevation,
            TenantScopeHelpers.ResolveRequestTenantId(http),
            applyTenantScope: true);

        if (publication is null || resource is null || !snapshot.IsRoutable(publication))
        {
            // Mirror the HTTP elevation surface, which 404s an unknown/retired
            // dataset. A NotFound here also avoids leaking whether a protected
            // layer exists to an unauthorized caller beyond what HTTP reveals.
            throw new RpcException(new Status(StatusCode.NotFound, $"Elevation layer {layerId} not found."));
        }

        return new ResolvedElevationLayer
        {
            LayerId = layerId,
            Snapshot = snapshot,
            Publication = publication,
            Resource = resource,
            Service = service,
        };
    }

    private static async Task<AuthorizedElevationLayer> AuthorizeAndResolveStorageLayerAsync(
        HttpContext http,
        ResolvedElevationLayer layer,
        CancellationToken cancellationToken)
    {
        await EnsureReadAccessAllowedAsync(http, layer, cancellationToken).ConfigureAwait(false);

        // ResolveV2TripleForTenant treats the protocol as a preference and falls back to another
        // publication of the same layer. HTTP elevation returns 404 when Elevation is disabled.
        if (!ServiceProtocols.IsProtocolEnabled(layer.Service, ServiceProtocols.Elevation))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Elevation is not enabled for this service."));
        }

        var storageLayerId = layer.Snapshot.ResolveStorageLayerId(layer.Publication, layer.Resource);
        if (!storageLayerId.HasValue)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Elevation layer {layer.LayerId} not found."));
        }

        return new AuthorizedElevationLayer(layer.Resource, storageLayerId.Value);
    }

    private static async Task EnsureReadAccessAllowedAsync(
        HttpContext http,
        ResolvedElevationLayer layer,
        CancellationToken cancellationToken)
    {
        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            http,
            layer.Resource,
            layer.Service,
            AccessPolicyHelpers.DefaultOperationForScope(AccessScope.Read),
            cancellationToken).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            var status = decision.RequiresAuthentication
                ? StatusCode.Unauthenticated
                : StatusCode.PermissionDenied;
            throw new RpcException(new Status(
                status,
                decision.FailureReason ?? "Access to this elevation layer is denied."));
        }
    }
}
