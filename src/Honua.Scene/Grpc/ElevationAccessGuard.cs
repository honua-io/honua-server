// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;

namespace Honua.Scene.Grpc;

/// <summary>
/// Enforces per-layer read authorization on the gRPC <c>ElevationService</c> so a
/// protected (non-public / role-restricted) elevation layer is gated identically
/// to the HTTP elevation surface. The HTTP adapter routes every request through
/// <see cref="LayerValidationHelpers.ValidateLayerWithAccessV2Async(HttpContext, int, AccessScope, string?, CancellationToken)"/>
/// at <see cref="AccessScope.Read"/> before querying; without this guard the gRPC
/// path would return elevation values for a layer the HTTP surface rejects,
/// violating the cross-protocol authorization rule (CLAUDE.md "Security/RBAC").
/// </summary>
/// <remarks>
/// Resolution and policy evaluation are delegated to the shared
/// <see cref="LayerValidationHelpers.EvaluateLayerReadAccessV2Async"/> seam (which
/// itself uses <c>AccessPolicyHelpers.EvaluateResourceAccessAsync</c> — the same
/// per-operation-grant-then-coarse-policy pipeline the HTTP validators use), so
/// HTTP and gRPC share one enforcement path rather than duplicating RBAC logic.
/// </remarks>
internal static class ElevationAccessGuard
{
    /// <summary>
    /// Resolves the elevation layer and enforces read access, throwing an
    /// <see cref="RpcException"/> when the layer is not found or access is denied.
    /// </summary>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="layerId">The publication layer index being queried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RpcException">
    /// Thrown with <see cref="StatusCode.NotFound"/> when the layer is absent or
    /// retired, <see cref="StatusCode.Unauthenticated"/> when authentication is
    /// required, <see cref="StatusCode.PermissionDenied"/> when the caller is
    /// authenticated but not authorized, or <see cref="StatusCode.Internal"/> when
    /// the HTTP request context is unavailable.
    /// </exception>
    /// <returns>
    /// The resolved (authorized) layer resource, so the caller can resolve the
    /// dataset's configured default mosaic merge strategy through the shared
    /// <c>RasterMosaicUtilities</c> exactly as the HTTP elevation path does.
    /// </returns>
    public static async Task<MetadataV2Resource> EnforceReadAccessAsync(
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

        return result.Resource;
    }
}
