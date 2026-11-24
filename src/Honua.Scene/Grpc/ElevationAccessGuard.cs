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

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            http,
            resource,
            service,
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

        // ResolveV2TripleForTenant treats the protocol as a preference and
        // falls back to another publication of the same layer. HTTP elevation
        // returns 404 when Elevation is not enabled; gRPC must do the same.
        if (!ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.Elevation))
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                "Elevation is not enabled for this service."));
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(publication, resource);
        if (!storageLayerId.HasValue)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Elevation layer {layerId} not found."));
        }

        return new AuthorizedElevationLayer(resource, storageLayerId.Value);
    }
}
