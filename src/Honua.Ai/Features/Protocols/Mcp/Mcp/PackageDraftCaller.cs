// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp;

internal static class PackageDraftCaller
{
    public static string? ResolveOwnerId(ClaimsPrincipal? principal) =>
        ServiceDataEditorAuthorization.ResolveOwnerSubject(principal);

    public static string RequireOwnerId(ClaimsPrincipal principal)
    {
        var ownerId = ResolveOwnerId(principal);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: true,
                "A package draft requires an authenticated owner.",
                OperatorResourceType.Package,
                OperatorOperation.Create);
        }

        return ownerId;
    }
}
