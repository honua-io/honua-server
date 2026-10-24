// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Resolves the stable subject stamped and compared as a feature or replica owner.
/// </summary>
internal static class EditOwnerSubject
{
    /// <summary>
    /// Returns <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then <c>api_key_id</c>,
    /// and only then <see cref="ClaimsIdentity.Name"/> when none of those exist.
    /// </summary>
    /// <param name="principal">The caller, or <c>null</c>.</param>
    /// <returns>The owner subject, or <c>null</c> when none is present.</returns>
    public static string? Resolve(ClaimsPrincipal? principal)
        => ServiceDataEditorAuthorization.ResolveOwnerSubject(principal);
}
