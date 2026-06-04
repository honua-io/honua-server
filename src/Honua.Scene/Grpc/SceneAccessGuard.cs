// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Scene.Grpc;

/// <summary>
/// Enforces per-scene read authorization on the gRPC scene/tile services so a
/// protected (non-public / role-restricted) scene is gated identically to the
/// HTTP and I3S scene surfaces. Both serve the same on-disk tilesets; without
/// this guard the gRPC path would return scene metadata and raw tile bytes to
/// anonymous or unauthorized callers that the HTTP surface rejects, violating
/// the cross-protocol authorization rule (CLAUDE.md "Security/RBAC").
/// </summary>
internal static class SceneAccessGuard
{
    /// <summary>
    /// Evaluates the resolved scene's <see cref="AccessPolicy"/> against the
    /// request principal at <see cref="AccessScope.Read"/> using the shared
    /// <see cref="AccessPolicyHelpers"/> pipeline, throwing an
    /// <see cref="RpcException"/> when access is denied. A <see langword="null"/>
    /// policy denotes a public scene and is always allowed, mirroring the HTTP
    /// adapters that only call <c>RequireAccess</c> when a policy is present.
    /// </summary>
    /// <param name="context">The active gRPC server call context.</param>
    /// <param name="accessPolicy">
    /// The resolved scene's access policy, or <see langword="null"/> for a
    /// public scene.
    /// </param>
    /// <exception cref="RpcException">
    /// Thrown with <see cref="StatusCode.Unauthenticated"/> when authentication
    /// is required, <see cref="StatusCode.PermissionDenied"/> when the caller is
    /// authenticated but not authorized, or <see cref="StatusCode.Internal"/>
    /// when the HTTP request context is unavailable.
    /// </exception>
    public static void EnforceReadAccess(ServerCallContext context, AccessPolicy? accessPolicy)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (accessPolicy is null)
        {
            // Public scene — no per-scene authorization to enforce.
            return;
        }

        var http = context.GetHttpContext();
        if (http is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Scene access context unavailable."));
        }

        var decision = AccessPolicyHelpers.EvaluateAccess(
            http,
            layerPolicy: accessPolicy,
            servicePolicy: null,
            scope: AccessScope.Read);

        if (decision.IsAllowed)
        {
            return;
        }

        var status = decision.RequiresAuthentication
            ? StatusCode.Unauthenticated
            : StatusCode.PermissionDenied;

        throw new RpcException(new Status(
            status,
            decision.FailureReason ?? "Access to this scene is denied."));
    }
}
