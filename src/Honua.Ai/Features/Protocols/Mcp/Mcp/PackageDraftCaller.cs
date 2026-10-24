// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Owner subject for <c>honua://map-packages/*</c> and <c>honua://app-packages/*</c>
/// drafts. The order matches attachment and replica ownership:
/// <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then <c>api_key_id</c>,
/// then <see cref="ClaimsIdentity.Name"/>.
/// </summary>
internal static class PackageDraftCaller
{
    /// <summary>Returns the owner subject, or null when the principal has none.</summary>
    public static string? ResolveOwnerId(ClaimsPrincipal? principal) =>
        ServiceDataEditorAuthorization.ResolveOwnerSubject(principal);

    /// <summary>
    /// Returns the owner subject or fails the save. A draft without an owner would
    /// be readable by every package reader, so the call does not continue.
    /// </summary>
    /// <param name="principal">Authenticated caller.</param>
    /// <returns>The owner subject to store.</returns>
    /// <exception cref="GeoprocessingAuthorizationException">The principal has no owner subject.</exception>
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
