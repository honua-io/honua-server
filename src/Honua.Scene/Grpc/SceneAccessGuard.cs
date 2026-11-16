// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Scene.Grpc;

internal static class SceneAccessGuard
{
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
