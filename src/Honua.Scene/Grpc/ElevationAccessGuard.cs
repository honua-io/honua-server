// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;

namespace Honua.Scene.Grpc;

internal static class ElevationAccessGuard
{
    public static async Task<MetadataV2Object> EnforceReadAccessAsync(
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

        var result = await LayerValidationHelpers.EvaluateLayerReadAccessV2Async(
            http,
            layerId,
            ServiceProtocols.Elevation,
            cancellationToken).ConfigureAwait(false);

        if (result.NotFound || result.Resource is null)
        {
            // Mirror the HTTP elevation surface, which 404s an unknown/retired
            // dataset. A NotFound here also avoids leaking whether a protected
            // layer exists to an unauthorized caller beyond what HTTP reveals.
            throw new RpcException(new Status(StatusCode.NotFound, $"Elevation layer {layerId} not found."));
        }

        if (!result.Decision.IsAllowed)
        {
            var status = result.Decision.RequiresAuthentication
                ? StatusCode.Unauthenticated
                : StatusCode.PermissionDenied;

            throw new RpcException(new Status(
                status,
                result.Decision.FailureReason ?? "Access to this elevation layer is denied."));
        }

        // EvaluateLayerReadAccessV2Async treats the protocol as a preference and
        // falls back to another publication of the same layer. HTTP elevation
        // returns 404 when Elevation is not enabled; gRPC must do the same.
        if (!ServiceProtocols.IsProtocolEnabled(result.Service, ServiceProtocols.Elevation))
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                "Elevation is not enabled for this service."));
        }

        return result.Resource;
    }
}
